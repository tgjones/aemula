# Oscilloscope Window — Implementation Plan

## Goal

Add a dockable `Aemula.UI` debugger window — modeled on a logic
analyzer/oscilloscope (Saleae-style) — that plots emulated signals over
time: CPU pins, bus values, and other "interesting" points per system.
Start digital/bus-only and simple; grow channel coverage, rendering
fidelity, and controls incrementally.

## Design decisions

These were confirmed up front since they shape the core data model:

- **Capture model: always-on rolling buffer, not arm/stop.** There's no
  separate "start capture" button. Recording happens for every tick the
  emulator actually executes, which — since all ticking already funnels
  through one place (`Debugger.RunForDuration` → `TickSystem()` →
  `System.Tick()`, confirmed as the only call site in the codebase today)
  — means the capture **freezes for free whenever the debugger is
  paused/stopped**, no extra plumbing needed. When running, the view
  auto-scrolls with "now" at the right edge; when stopped, it holds still
  and you pan/zoom through what's already in the buffer.
- **Bus channels read the chip's existing combined value directly.**
  E.g. the address bus channel samples `Cpu.Address` (already a `ushort`
  property) once per tick, rather than modeling 16 individual bit-signals
  and compositing them. This matches how every chip in the codebase
  already exposes its pins (`Mos6502Chip.Address`, `.Data`, `.RW`, ...) and
  keeps the channel model to two kinds instead of three.
- **Analog channels are deferred entirely.** `Television`/`Oscillator` in
  `Emulation/Output/Television.cs` are still `NotImplementedException`
  stubs — there's no real analog signal (e.g. composite video) to sample
  yet. The channel model starts digital + bus only; a float-sampled analog
  kind gets added when composite video output becomes real work, not
  before.

## Architecture

New code lives in `src/Aemula/UI/Oscilloscope/`, following the existing
`Aemula/UI/*Window.cs` pattern (`DebuggerWindow` subclass, registered per
system from that system's `Debugger.CreateDebuggerWindows`, same as
`CpuStateWindow`/`ScreenDisplayWindow` today).

### Rendering: Hexa.NET.ImPlot

Waveform rendering uses [Hexa.NET.ImPlot](https://www.nuget.org/packages/Hexa.NET.ImPlot/),
pinned to **2.2.9** to match the existing `Hexa.NET.ImGui` version, added
to `Directory.Packages.props` and referenced from `Aemula.csproj` next to
the other `Hexa.NET.*` packages. This is a new dependency — nothing in the
project uses ImPlot today, so Phase 1 should budget a short spike to learn
its API/idioms (multi-axis stacking, custom draw-list access for the bus
bands) before committing to the final render structure.

Using ImPlot instead of hand-rolled `ImDrawList` calls (the style
`TiaWindow`/`ScreenDisplayWindow` otherwise use) means:

- Digital channels render as step-line plots (`PlotStairs` or equivalent)
  instead of manually computing pixel transitions.
- Pan/zoom/scroll on the time axis comes from ImPlot's own axis
  interaction (drag to pan, scroll/box-select to zoom) instead of
  hand-written pixel math — our job is mostly constraining it (clamp
  panning to what the ring buffer actually retains, format the axis in
  time units via `CyclesPerSecond`, and a "jump to now" button that resets
  the x-axis to the live edge).
- Bus (hex-band) rows are the one part ImPlot doesn't give us out of the
  box — those still need custom drawing via ImPlot's own plot draw list
  (drawing into plot space, not screen space, so it stays aligned with the
  shared time axis), covered in Phase 2.

### Channel model

```
ScopeChannelNode              (abstract: shared Name)
├─ ScopeChannel                (leaf: one sampled signal)
│    Kind: Digital | Bus
│    BitWidth: int             (1 for Digital, e.g. 8/16 for Bus)
│    Read: Func<ulong>         (digital reads back as 0/1)
└─ ScopeChannelGroup           (composite: ordered List<ScopeChannelNode>)
                                (renders as a collapsible header in the
                                 sidebar; collapsing hides member rows,
                                 it doesn't stop recording them)
```

Keeping the sample type as a single `ulong` regardless of `Kind` lets the
recorder and ring buffer stay generic — `Kind`/`BitWidth` only matter to
the renderer (step-trace vs. hex band) and to the UI (checkbox per leaf
node to show/hide its row).

### Recording

`ScopeRecorder` is constructed from a flattened list of leaf
`ScopeChannel`s and owns one fixed-depth ring buffer (`ulong[]`) per
channel, plus a shared write cursor. `Sample()` reads every channel's
`Read()` and appends. Capacity is a tunable constant to start (a few
hundred thousand samples costs low tens of MB total across a dozen
channels — cheap; not a design axis worth locking down now).

To drive `Sample()` once per tick, `Debugger` gets a small hook:

```csharp
public event Action? Ticked;   // raised in RunForDuration, right after TickSystem()
```

`OscilloscopeWindow` subscribes in its constructor and no-ops inside the
handler when `!IsOpen` — same "skip when not open" convention
`DebuggerWindow.Prepare`/`Draw` already use, so there's no separate
subscribe/unsubscribe lifecycle to manage.

### Window layout

- Left sidebar: the channel tree (`ScopeChannelGroup` as a
  `CollapsingHeader`/`TreeNode`, `ScopeChannel` leaves as rows with a
  show/hide checkbox).
- Main pane: one horizontal row per visible leaf channel.
  - Digital rows: classic high/low step trace.
  - Bus rows: horizontal hex-value bands, edges where the value changes
    (Saleae-style parallel bus view), value shown as text inside each
    constant-value segment.
- Toolbar: zoom (samples/pixel) and a time ruler derived from
  `EmulatedSystem.CyclesPerSecond`, plus a "jump to now" button for after
  you've panned back through history while paused.

### Chips own their channel group; systems compose

Mirroring the existing `internal void CreateDebuggerWindows(List<DebuggerWindow> result)`
method already on `Mos6502Chip` (called by `AppleIIDebugger` as
`_appleII.Cpu.CreateDebuggerWindows(result)`), each CPU chip gets a
matching `internal ScopeChannelGroup CreateScopeChannelGroup()`, defined
once on the chip itself and reused by every system that embeds it. Today
that's just `AppleIISystem`, but a `Mos6502Chip` is also used elsewhere
in the codebase (e.g. `Atari2600System`'s `Mos6507`, `Nes`) — without this,
each of those systems would otherwise redefine the same Address/Data/RW/...
channel list. This is why the request came up: **the CPU class owns its
own pins, once.**

A system then composes its root group from its chip(s) plus whatever
system-level glue signals it wants to add — e.g. on `AppleIISystem`:

```csharp
internal ScopeChannelGroup CreateScopeChannelGroup()
{
    return new ScopeChannelGroup("Apple II",
    [
        Cpu.CreateScopeChannelGroup(),
        new ScopeChannelGroup("Video Timing",
        [
            ScopeChannel.Digital("HBL", () => Hbl),
            ScopeChannel.Digital("VBL", () => Vbl),
            ScopeChannel.Digital("Color Burst Gate", () => ColorBurstGate),
            ScopeChannel.Digital("Phase 0", () => Phase0),
        ]),
    ]);
}
```

`AppleIIDebugger.CreateDebuggerWindows` then just does
`result.Add(new OscilloscopeWindow(this, _appleII.CreateScopeChannelGroup()));`,
same shape as how it already assembles `BreakpointsWindow`/`MemoryEditor`/
`ScreenDisplayWindow` today.

**Starter list for Apple II** (everything below is already a public
member — no new pin exposure needed to get started):

| Group | Channel | Kind | Source | Owned by |
|---|---|---|---|---|
| MOS6502 | Address | Bus (16-bit) | `Address` | `Mos6502Chip.CreateScopeChannelGroup()` |
| MOS6502 | Data | Bus (8-bit) | `Data` | `Mos6502Chip.CreateScopeChannelGroup()` |
| MOS6502 | R/W | Digital | `RW` | `Mos6502Chip.CreateScopeChannelGroup()` |
| MOS6502 | SYNC | Digital | `Sync` | `Mos6502Chip.CreateScopeChannelGroup()` |
| MOS6502 | RDY | Digital | `Rdy` | `Mos6502Chip.CreateScopeChannelGroup()` |
| MOS6502 | IRQ | Digital | `Irq` | `Mos6502Chip.CreateScopeChannelGroup()` |
| MOS6502 | NMI | Digital | `Nmi` | `Mos6502Chip.CreateScopeChannelGroup()` |
| MOS6502 | PHI2 | Digital | `Phi2` | `Mos6502Chip.CreateScopeChannelGroup()` |
| Video Timing | HBL | Digital | `AppleIISystem.Hbl` | `AppleIISystem.CreateScopeChannelGroup()` |
| Video Timing | VBL | Digital | `AppleIISystem.Vbl` | `AppleIISystem.CreateScopeChannelGroup()` |
| Video Timing | Color Burst Gate | Digital | `AppleIISystem.ColorBurstGate` | `AppleIISystem.CreateScopeChannelGroup()` |
| Video Timing | Phase 0 | Digital | `AppleIISystem.Phase0` | `AppleIISystem.CreateScopeChannelGroup()` |

More glue signals (e.g. the address-decoder chip outputs currently held
as private fields on `AppleIISystem`) can be added later by bumping the
relevant field to `internal`/`public`, same incremental pattern the rest
of the debugging UI already follows — not a blocker for the starter list.
Other chips (`Intel8080`, `Ricoh2C02`, ...) can grow their own
`CreateScopeChannelGroup()` the same way, on their own schedule — nothing
about the framework requires every chip to have one from day one.

## Phased plan

**Phase 0 — Scaffolding**
Add the `Hexa.NET.ImPlot` 2.2.9 package reference. `ScopeChannel`/
`ScopeChannelGroup` types, `ScopeRecorder` ring buffer, `Debugger.Ticked`
event, `Mos6502Chip.CreateScopeChannelGroup()` and
`AppleIISystem.CreateScopeChannelGroup()` per the composition above, and a
minimal `OscilloscopeWindow` that just lists channel names (no waveform
drawing yet) to prove the plumbing end to end. Wire into `AppleIIDebugger`.

**Phase 1 — Digital waveform rendering**
Spike on `Hexa.NET.ImPlot` basics (a plot per channel row vs. one shared
plot with linked/stacked axes — whichever gives cleaner synced pan/zoom
across all rows), then draw real step-trace rows for `Digital` channels
via `PlotStairs` (or equivalent). Right-anchored to "now" while the
debugger runs, holding still when it's stopped. Sidebar show/hide
checkboxes and group collapse/expand.

**Phase 2 — Bus channel rendering**
Hex-banded display for `Bus` channels (Address/Data), edges at
value-change points, hover tooltip with the exact value.

**Phase 3 — Timescale controls**
Most of this comes from ImPlot's own x-axis interaction; the work here is
constraining/formatting it — time-unit axis labels (derived from
`CyclesPerSecond`), clamping pan to what the ring buffer retains, and a
"jump to now" button that resets the axis to the live edge.

**Phase 4 — Polish**
Measurement cursors (stretch), persisting per-channel visibility/group
collapse state across sessions (via the existing `ImGuiSettingsHandler`
plumbing already used for window open/closed state in `Program.cs`),
per-channel trace colors.

**Phase 5 (stretch, later) — Analog channels**
Add the float-sampled `Analog` channel kind once composite video/
`Television` produces a real signal to sample. Not started until that
groundwork exists.

**Phase 6 (stretch, later) — More systems**
Same framework is already system-agnostic by Phase 0 — add a system-level
`CreateScopeChannelGroup()` (composing that system's chips, most of which
already have one by this point) + one line in the relevant system's
`Debugger.CreateDebuggerWindows`, for NES/Chip8/Atari2600/etc. The
`Atari2600`'s `Mos6507` and `Nes`'s CPU can both reuse
`Mos6502Chip.CreateScopeChannelGroup()` directly if they wrap the same
chip class — worth checking when this phase starts.

## Open risks

- This is the project's first use of ImPlot — budget the Phase 1 spike
  seriously rather than assuming the API maps 1:1 onto the hand-rolled
  `ImDrawList` patterns used elsewhere in the codebase.
- Ring buffer capacity is a guessed starting constant; if it's too short
  to be useful when zoomed out, or too large for no benefit, tune it once
  Phase 1 is actually on screen rather than guessing further now.
- Bus-band rendering (Phase 2) is the fiddliest drawing code here — it's
  also the one piece ImPlot doesn't hand us for free (custom draw calls
  into plot space). Worth prototyping against just the Data bus (8-bit,
  narrower) before Address (16-bit).
- `Debugger.Ticked` fires every tick the emulator executes, including
  fast-forwarded/non-stepped runs — if that turns out to add measurable
  overhead even with `OscilloscopeWindow` closed (it shouldn't, since the
  handler no-ops on `!IsOpen`, but worth a sanity check once real channel
  counts exist), it's an easy escape hatch to only subscribe while open
  instead of always-subscribed-but-no-op.
