# Aemula.Console MCP server — Implementation Plan

## Execution notes

When told to execute this plan: work through the phases below autonomously,
committing once each phase is done and working rather than batching
everything into one commit or stopping for approval between phases. Code
comments must stand on their own — explain the *why* directly in the
comment, never by citing this document or a phase number, since this file
is deleted once the plan lands (same convention as every other landed plan
in this repo). If anything encountered during implementation calls for
deviating from what's written here, stop and ask rather than improvising
past it. Before writing any MCP SDK code, verify the current
`ModelContextProtocol` package's API shape (tool-registration attributes,
`AddMcpServer`/transport extension method names) against its actual NuGet
package/samples rather than this document — package surface for MCP
server SDKs is young enough to have moved since this plan was written.

## Goal

Give an agent (Claude Code, or any other MCP client) a way to start an
Aemula system headlessly and drive/introspect it at runtime — step
cycles or frames, read/write memory, set breakpoints, disassemble, inject
input, take screenshots — without hand-writing special-cased logging into
the emulator for each investigation. Today `Aemula.Console` is a one-shot
batch process (`Program.cs`: parse args, run N frames via `FrameRunner`,
print one JSON summary line, exit); it never constructs a `Debugger` and
has no IPC surface at all beyond its own `--input` scripting DSL. This
plan adds an `mcp` mode that instead starts a long-lived MCP server over
stdio, with a tool set that is generic across every system because it's
built against the existing `Debugger`/`Rig` base types rather than any
concrete system.

Explicitly not part of this plan (see "Explicitly out of scope" for the
full list): an MCP-hosted mode in `Aemula.UI` — the visible-window
sibling of this feature, needing a socket/HTTP transport instead of
stdio since a human launches the UI and an agent attaches after the fact,
rather than the agent spawning the process itself. That's a separate plan
that reuses the tool implementations this one builds. Also out of scope:
anything that has `Aemula.Console` itself call out to an LLM API
(screenshot verification, autonomous play) — this plan is exclusively
about exposing tools, not consuming a model.

## Design

### A new mode on the existing binary, not a new project

`aemula-console mcp <system> [--media <bay>=<path>] [--slot <slot>=<card>]`
— a new mode alongside the existing `<system> --frames <n>`,
`--list-slots`, and `--list-media` modes in `Program.cs`. It builds the
`Rig` exactly the way the existing batch mode does: `EmulatedSystems
.FindById`, `descriptor.Build`, applying `--media`/`--slot`, and the
required-bay check. That construction sequence is currently inlined in
`Run`; factor it into a shared private helper both modes call, rather than
duplicating it. Once the `Rig` exists, batch mode still hands it to
`FrameRunner.Run` as today; `mcp` mode instead hands it to a new
`McpServer` that runs until stdin closes.

### `Debugger` is already generic — the tool layer needs no per-system code

`EmulatedSystem.CreateDebugger()` is already implemented by every system
(`AppleISystem`, `AppleIISystem`, `Atari2600System`, `NesSystem`,
`SpaceInvadersSystem`, `ZX80System`) and returns the common `Debugger`
base type, which already exposes everything this plan's tool set needs,
uniformly: `RunForDuration`/stepping, `Breakpoints` (add/toggle/remove
execution and byte/word-value breakpoints), `Disassembler.Cache`,
`MemoryCallbacks` (`Read`/`Write`/`ReadWord`), `LastPC`, `Stopped`,
`StepModes`. `Aemula.UI`'s `DebuggerUIFactory` only needs a per-system
`switch` because it's choosing *UI widget* types
(`AppleIDebuggerUI` vs `ZX80DebuggerUI`, ...); the underlying `Debugger`
surface this plan drives is already the same shape for every system. So
the tool implementations are written once, against `Debugger`/`Rig`, and
work for every system without a dispatch switch — the same way
`FrameRunner`/`InputScript`/`ScreenshotWriter` already work uniformly
across systems today.

A handful of gaps need small, generic additions to close — not
per-system special-casing, just filling in a hook the same way
`Debugger.CreateDisassembler()`/`TickSystem()` are already virtual hooks
each subclass fills in:

- **Tick-count-bounded running.** `Debugger.RunForDuration(TimeSpan)`
  converts to a tick count internally (`duration.ToSystemTicks(...)`)
  and loops `for (i = 0; i < clocks && !Stopped; i++)`. What the tools
  actually want ("run N cycles", "run one frame at a time") is that loop
  addressed directly by tick count, not round-tripped through
  `TimeSpan`. Extract the loop body into a new `RunTicks(ulong clocks)`
  that returns the number of ticks actually executed (so a caller can
  tell "ran the full request" from "stopped early"); `RunForDuration`
  becomes a thin wrapper: `RunTicks(duration.ToSystemTicks(...))`. Pure
  refactor, no behavior change.
- **Frame-boundary running.** Nothing on `Debugger` detects a video
  frame boundary — `FrameRunner` does that itself, watching
  `television.CurrentRow` wrap on every raw `system.Tick()`, deliberately
  bypassing the debugger to avoid breakpoint/step overhead in batch runs.
  An MCP "run one frame, but stop early if a breakpoint fires" tool wants
  both: keep this in `Aemula.Console` (not core `Debugger` — it's
  `Television`-specific, not something every `Debugger` consumer needs),
  as a loop calling `debugger.RunTicks(1)` once per iteration, checking
  `rig.Television.CurrentRow` for a wrap the same way `FrameRunner` does,
  and breaking out early if `debugger.RunTicks` results in
  `debugger.Stopped == true`.
- **`Stopped` semantics, used as the general "did I stop early" signal.**
  `Stopped` starts `true` (per the constructor) and is set back to `true`
  only when a breakpoint or the active step mode's `ShouldStop` fires
  mid-loop — a run that completes its full requested tick count without
  either never sets it, so it stays whatever it was going in. Every
  execution tool must set `debugger.Stopped = false` immediately before
  calling `RunTicks`, then after the call, `debugger.Stopped` tells the
  caller "stopped early (breakpoint/step), not because you ran out of
  budget" — exactly the signal an agent needs to know whether it hit
  something interesting.
- **Stepping.** `StepModes`/`ActiveStepModeIndex` already exist and are
  already populated per system (`Mos6502Debugger.RegisterStepModes`
  contributes "Step Instruction"/"Step CPU Cycle"; system debuggers add
  more — `Atari2600Debugger` adds "Step Color Cycle",
  `NesDebugger` adds "Step PPU Cycle"). A generic `step(mode)` tool looks
  up a step mode by its `Label` in `debugger.StepModes`, calls its
  `Setup?.Invoke()`, sets `ActiveStepModeIndex` to that index and
  `Stopped = false`, then calls `RunTicks` with a generous upper bound
  (it stops itself once `ShouldStop()` fires). A `list_step_modes()`
  tool exposes the labels so an agent discovers valid `step(mode)` values
  per system rather than hardcoding them.
- **Registers.** There is no existing generic "dump CPU state" surface —
  `LastPC` is the only register-shaped thing on the base `Debugger`, and
  full register sets are inherently chip-specific (Mos6502's A/X/Y/SP/P
  vs. Intel8080's vs. Z80's). Add a virtual
  `Debugger.DescribeRegisters()` returning
  `IReadOnlyList<(string Name, string Value)>` (default `[]`), and
  implement it once each on the two existing *chip-level* debuggers,
  `Mos6502Debugger` and `Intel8080Debugger` — which cascades to every
  system built on them (`AppleI`, `AppleII`, `Atari2600`, `Nes` via
  Mos6502; `SpaceInvaders` via Intel8080) without touching each system
  debugger individually. `ZX80` has no chip-level `Z80Debugger` class to
  hang this off yet, so `get_registers()` returns empty for ZX80 in this
  plan — a known, explicit gap (see "Explicitly out of scope"), not a
  silent omission.
- **Listing breakpoints.** `BreakpointManager.NumBreakpoints` and
  `GetBreakpoint(int)` are `internal`, and `Aemula.Console` has no
  `InternalsVisibleTo` grant into `Aemula` (only `Aemula.Tests` and
  `aemula-ui` do). Rather than widen that grant to a third assembly for
  one feature, add a public
  `BreakpointManager.List()` returning a small public
  `BreakpointInfo` record snapshot (address, type label, condition,
  value, enabled) — a genuinely useful public API on its own, not an
  internals leak.
- **Screenshots as bytes, not just files.** `ScreenshotWriter.Write`
  builds an `Image<Rgba32>` and immediately calls `image.SaveAsPng(path)`
  — there's no way to get the encoded bytes back without a file on disk,
  and an MCP `screenshot` tool wants to return image content directly to
  the client. Factor the pixel-buffer construction out into a method
  returning the built `Image<Rgba32>`; `Write(television, path)` and a
  new `Capture(television)` (returns PNG bytes via
  `image.SaveAsPng(stream)` into a `MemoryStream`) both become thin
  wrappers over it.
- **Live input, vs. `InputScript`'s pre-scheduled batch replay.**
  `InputScript` is built around "compute every event's frame offset up
  front, then replay against `OnFrameCompleted`" — the right model for
  `--input`, wrong model for an interactive tool call that happens *now*.
  What's reusable is its lower-level per-event logic: the private
  `Apply` (dispatches a token to either a `ConsoleControl` or a joystick/
  button key event) and `TypeCharacter` (one keyboard character's
  key-down/up), plus its `FramesPerTypedKey`/`TypedKeyHoldFrames` timing
  constants and the `knownTokens` computation currently inlined in
  `Parse`. Factor those four into public statics on `InputScript` (or a
  small shared type if that reads better once written) so both
  `InputScript.Parse`'s batch path and the new interactive input tools
  call the same underlying logic — the interactive tools just interleave
  those calls with `run_frames` calls they issue themselves instead of
  pre-computing offsets.

### MCP hosting

Use the `ModelContextProtocol` NuGet package (official C# SDK for MCP)
with the stdio transport — `AddMcpServer().WithStdioServerTransport()`
plus attribute-based tool registration (`[McpServerToolType]`/
`[McpServerTool]`), matching the package's documented pattern as of
implementation time (see the Execution notes above — verify the exact
API shape against the package itself, not this document). `Aemula.Console`
is a single-system, single-session process per invocation, so the tool
class(es) can take the constructed `Rig`/`Debugger` as constructor-
injected singletons rather than needing any session/multi-tenancy
handling. Add `ModelContextProtocol` to `Directory.Packages.props`
(central package management is already in effect for this repo) and
reference it from `Aemula.Console.csproj` only — this is not a dependency
`Aemula`/`Aemula.UI` need.

### Tool set (v1)

**Lifecycle** — `reset()`, `insert_media(bay, path)`, `eject_media(bay)`,
`list_media_bays()`, `get_status()` (system id, `Stopped`, `LastPC`,
`System.TotalCycles`, current television row).

**Introspection** — `read_memory(address, length)`,
`write_memory(address, bytes)`, `disassemble(address, count)` (ensures
each address is cached via `Disassembler.OnAddressExecuting`, then reads
`Disassembler.Cache`), `get_registers()`, `list_breakpoints()`,
`add_execution_breakpoint(address)`,
`add_value_breakpoint(kind, address, condition, value)`,
`remove_breakpoint(index)`, `screenshot()` (returns PNG image content;
optionally also writes to a given path).

**Execution & input** — `run_cycles(count)`, `run_frames(count)`,
`list_step_modes()`, `step(mode)`, `list_input_tokens()`,
`press_input(token)`, `release_input(token)`, `type_text(text)`.

System selection stays a startup argument (`aemula-console mcp
<system>`), matching the existing CLI's one-system-per-invocation model —
no runtime "switch system" tool in this plan. (`Aemula.UI`'s
`DebuggerHost.SetSystem` already supports swapping systems live, but
that's a UI-specific capability tied to a persistent window; revisit only
if the future socket-hosted UI variant needs it.)

## Phased plan

**Phase 0 — Dependency + skeleton `mcp` mode**

Add `ModelContextProtocol` to `Directory.Packages.props` and
`Aemula.Console.csproj`. Factor `Program.Run`'s "build a `Rig` from parsed
options" block (descriptor lookup, `Build`, media/slot application,
required-bay check) into a shared helper used by both the existing batch
path and the new `mcp` mode. Add `mcp` parsing to `ParseArgs`
(`aemula-console mcp <system> [--media ...] [--slot ...]`). New
`McpServer` type in `Aemula.Console` that takes the built `Rig`,
constructs its `Debugger` via `rig.System.CreateDebugger()`, and starts a
stdio MCP server with no tools registered yet. Verify: launch
`aemula-console mcp applei`, confirm an MCP client (the MCP inspector, or
Claude Code itself) connects over stdio and sees the server with an empty
tool list; confirm the existing batch mode still works unchanged.

**Phase 1 — Core additions the tools depend on**

- `Debugger`: extract `RunTicks(ulong clocks)` out of
  `RunForDuration(TimeSpan)` (returns ticks actually executed;
  `RunForDuration` becomes a thin wrapper). Verify no behavioral change —
  existing UI stepping/breakpoints still work identically.
- `Debugger.DescribeRegisters()` virtual hook (default `[]`); implement
  on `Mos6502Debugger` and `Intel8080Debugger`.
- `BreakpointManager.List()` returning the new public `BreakpointInfo`
  snapshot record.
- `ScreenshotWriter`: factor pixel-buffer construction out of `Write`
  into a method returning `Image<Rgba32>`; add `Capture(television)`
  returning PNG bytes.
- `InputScript`: make `Apply`, `TypeCharacter`,
  `FramesPerTypedKey`/`TypedKeyHoldFrames`, and the known-tokens
  computation reusable (public statics, or extracted to a small shared
  type) without changing `Parse`'s existing behavior.

**Phase 2 — Introspection tools**

`read_memory`, `write_memory`, `disassemble`, `get_registers`,
`list_breakpoints`, `add_execution_breakpoint`, `add_value_breakpoint`,
`remove_breakpoint`, `screenshot`, `get_status`. Each is a thin adapter
over Phase 1's surface — no new core logic beyond what Phase 1 added.

**Phase 3 — Execution & input tools**

`run_cycles`, `run_frames` (the frame-boundary loop described in
Design, layered over `RunTicks`), `list_step_modes`, `step`,
`list_input_tokens`, `press_input`, `release_input`, `type_text`
(interleaving Phase 1's factored-out input helpers with `run_frames`
calls the tool issues itself), `reset`, `insert_media`, `eject_media`,
`list_media_bays`.

**Phase 4 — Manual verification**

Connect an MCP client (Claude Code, in this repo) to `aemula-console mcp
applei` and exercise the full tool set on a real boot: reset, `type_text`
a short BASIC program, `run_frames`, `screenshot`, set a breakpoint,
`run_cycles` to it, `get_registers`/`read_memory`, `disassemble` around
`LastPC`. Repeat a spot-check on at least one system per other chip
family in scope (`spaceinvaders` for Intel8080; `atari2600` or `nes` for
a second Mos6502 system) to confirm genuine cross-system genericity, not
just Apple I. Confirm the existing batch mode (`--frames`, `--list-slots`,
`--list-media`) still works unchanged after Phase 0/1's refactors.

## Testing

`RunTicks`/`RunForDuration`'s split, `BreakpointManager.List()`,
`Debugger.DescribeRegisters()` on the two chip debuggers, and the
frame-boundary loop's tick-vs-row-wrap logic are the pieces here with
real logic worth unit testing in `Aemula.Tests`, which already references
`Aemula.csproj`. The MCP tool methods themselves are thin adapters with
no independent logic once the above are tested — verify those manually
per Phase 4, the same way this repo already verifies `Aemula.Console`'s
existing CLI behavior and `Aemula.UI` changes.

## Explicitly out of scope

- **`Aemula.UI` hosting** (socket/HTTP transport, human-visible window
  with an agent driving the same underlying tools) — a separate plan,
  reusing this plan's tool implementations once they exist.
- **Any `Aemula.Console`-initiated call to an LLM API** (screenshot
  verification, autonomous play) — this plan is tools-only; it never
  consumes a model itself.
- **Runtime system switching within one MCP session** — matches
  Console's existing one-system-per-process model.
- **ZX80/Z80 register dump** — no chip-level `Z80Debugger` class exists
  yet to hang `DescribeRegisters()` off; `get_registers()` returns empty
  for ZX80 until that's added, separately.
- **Authentication/access control on the stdio transport** — a local
  subprocess the client spawns and owns the pipes of is the same trust
  boundary as any other local dev tool a client shells out to.
