# Call stack window — Implementation Plan

## Execution notes

When told to execute this plan: work through the phases below autonomously,
committing once each phase is done and working rather than batching
everything into one commit or stopping for approval between phases. Code
comments must stand on their own — explain the *why* directly in the
comment, never by citing this document or a phase number, since this file
is deleted once the plan lands (same convention as every other landed plan
in this repo). If anything encountered during implementation calls for
deviating from what's written here, stop and ask rather than improvising
past it.

## Goal

None of the six systems' debuggers (`Atari2600`, `Nes`, `AppleI`, `AppleII`,
`SpaceInvaders`, `ZX80`) show call-stack depth today — `DisassemblyWindow`
shows only the current instruction, so answering "how did execution get
here" means single-stepping backward by hand or reading the raw stack bytes
in `MemoryEditor`. This plan adds a `CallStackWindow` that lists the active
chain of subroutine calls (return address, called address, and its label if
the disassembler has one) for whichever CPU family the running system uses
— Mos6502, Z80, or Intel8080 — following this repo's existing
`Aemula`/`Aemula.UI` split: the tracking logic (a shadow call stack built
from live execution) lives in `Aemula.Debugging`, chip-agnostic; only the
ImGui rendering lives in `Aemula.UI`.

### Relationship to the other two open plans

- `docs/disassembly-window-plan.md` — orthogonal, as expected: it's about
  `DisassemblyWindow`'s row rendering, colors, and live per-address
  instrumentation. One real touchpoint: its Phase 2a-2c adds
  `MnemonicCategory.Call`/`MnemonicCategory.Return`, a general-purpose
  mnemonic classification. This plan does **not** wait for or depend on
  that work — see Design below for why a narrower, purpose-built signal is
  used instead — but if Phase 2a-2c has already landed by the time this
  plan is executed, its categorization and this plan's are answering
  different questions (display category vs. "does this instruction move
  the shadow stack") and can coexist without either needing to change.
- `docs/disassembly-cache-invalidation-plan.md` — orthogonal in effect
  (that plan is about stale cached disassembly text on self-modifying
  writes; this plan is about tracking calls/returns), but both plans touch
  the same fetch-detection call sites: each system's `TickSystem` override
  (or, if that plan has already landed, the consolidated
  `Mos6502Debugger.Tick`/`Intel8080Debugger.Tick`/`Z80Debugger.Tick`
  helpers). This plan's own changes to those call sites are additive (see
  Phase 2) and independent of execution order — whichever plan lands
  second just touches the lines the first one left in whatever shape it
  left them. Worth checking `git log` for which has landed before starting,
  purely so the diff is against current code, not stale expectations from
  this doc.

## Design

### What "call" and "return" mean here, and why SP tracking replaces explicit return-instruction detection

A shadow call stack needs to know two things as execution runs: when a new
frame was entered (a call), and when one was left (a return). The natural
first idea — classify every instruction as call-like or return-like, the
same way `JumpTarget.Type == Call` already exists for jump targets — turns
out to only solve half the problem well:

- **Detecting calls this way works cleanly.** `JumpTarget(JumpType.Call, ...)`
  is already produced by the disassembler at the moment a JSR/CALL/RST is
  disassembled — a one-time, static, per-opcode classification, exactly the
  kind of thing the disassembler is already built to do.
- **Detecting returns this way is chip-dependent, and outright wrong for
  one of the three.** 6502 and Z80 do give a subroutine return its own
  opcode, separate from the interrupt-return opcode: 6502 `RTS` (0x60) vs.
  `RTI` (0x40); Z80 `RET`/conditional-`RET` (0xC0-0xF8) vs. the ED-prefixed
  `RETI`/`RETN`. A classifier that only treats `RTS`/`RET` as a pop trigger
  and leaves `RTI`/`RETI`/`RETN` alone would, on those two chips, never
  confuse the two — *as long as interrupt entry is also never pushed as a
  frame*, which this plan already doesn't do (see below), the two paths
  just don't interact. **Intel8080 has no such distinction at all** —
  there is no separate return-from-interrupt opcode; an interrupt handler
  returns via the exact same `RET`/conditional-`RET` family as any
  ordinary subroutine, because the hardware itself doesn't disambiguate
  the two cases. On 8080, entering an interrupt handler (the interrupting
  device jams a byte, normally an RST, directly onto the data bus — not
  something the disassembler can classify from memory contents, since
  `_cpu.Address` still just points at whatever ordinary instruction real
  memory has there) never pushes a frame, but that handler's `RET` would,
  under pure opcode classification, still fire the pop path and delete the
  *caller's* real frame.
- **Even where the opcode distinction exists (6502, Z80), it only protects
  against interrupts — not against anything else that moves SP without
  going through a classified instruction.** A tail call (`JMP`/`JP` used
  instead of `RTS`/`RET` to "return," reusing the return address already on
  the stack), or code that manipulates SP directly to discard/skip a frame,
  desyncs a pure push/pop counter permanently — nothing ever tells it a
  frame is gone. A single shared mechanism that also fixes this for free
  is worth more than a correct-but-narrower per-chip return-opcode table.

Instead: **pop is driven by the live stack pointer, not by classifying
which instruction executed.** Every chip family here has SP-relative
push/pop semantics where the stack grows downward (SP decreases on push,
increases on pop) — `Mos6502Chip.SP` (a `byte`, page-1 offset),
`Z80Chip.SP`/`Intel8080Chip.SP` (`SPRegister`, full 16-bit). Each pushed
frame records the live SP value at the moment it was pushed (i.e., the
depth the call reached); on every instruction fetch, before doing anything
else, pop any frame whose recorded depth has been reached or passed by the
current live SP:

```csharp
// Aemula/Debugging/Debugger.cs
while (CallStack.TryPeek(out var frame) && StackPointer.Read() >= frame.StackPointerAtCall)
{
    CallStack.Pop();
}
```

This is self-healing by construction, which is what makes it safe to leave
hardware interrupts unmodeled in v1 (see Explicitly out of scope) instead
of chasing three different per-chip interrupt-acknowledge signals: an
interrupt this plan doesn't push a frame for just doesn't show up as a
frame, and its RTI/RETN/RETI doesn't wrongly pop one either, because the
trim condition only fires when SP has actually recovered past a
*real* recorded frame. The same loop also absorbs every other way a shadow
stack can otherwise desync from reality — a tail-call `JMP`/`JP` used
instead of `RTS`/`RET` to "return", hand-rolled stack manipulation, an
`RTI`/`RETN` that unwinds more than one level — without needing to special-
case any of them. The `while` (not `if`) matters: a single event can make
SP jump past more than one recorded frame at once (e.g. code that
manipulates SP directly to unwind several levels in one step), and all of
those frames are equally stale.

Push stays classification-based, using the disassembler's existing
`JumpTarget`. Both pieces together replace today's `OnAddressExecuting`:

```csharp
// Aemula/Debugging/Debugger.cs
protected void OnAddressExecuting(ushort address)
{
    var previousPC = LastPC;
    LastPC = address;

    Disassembler.OnAddressExecuting(address);

    while (CallStack.TryPeek(out var frame) && StackPointer.Read() >= frame.StackPointerAtCall)
    {
        CallStack.Pop();
    }

    if (Disassembler.Cache[previousPC].Instruction is { JumpTarget.Type: JumpType.Call } previous)
    {
        CallStack.Push(new CallStackFrame(previousPC, address, previous.Next!.Value, StackPointer.Read()));
    }
}
```

The trim runs *before* the push check so a call made from inside a frame
that's simultaneously being returned from at the same depth (unusual, but
possible with hand-rolled stack tricks) still ends up in the right order.
`Cache[previousPC].Instruction` is guaranteed already populated by this
point — it was disassembled during its own fetch, when it was itself the
`address` argument to an earlier call to this same method — so no ordering
dependency on the `Disassembler.OnAddressExecuting(address)` call just
above; that call is only disassembling the *new* address. On the very
first call this session (`previousPC` still its default `0`), `Cache[0].Instruction`
is `null` unless something coincidentally already executed at address
`0x0000`, so the push check simply does nothing — correct, since there's no
real "previous instruction" yet.

`previous.Next` is the instruction after the call — guaranteed non-null for
every `JumpType.Call` site once Phase 1 below fixes the two chips where
that isn't already true. Reading `StackPointer.Read()` here (after the
call instruction has finished executing, at the moment the callee's first
opcode is being fetched) captures exactly the depth that instruction's own
return address is sitting behind — precisely the value the trim loop above
needs to compare against.

A capped depth (`const int MaxDepth = 256`) guards against unbounded growth
from pathological code (e.g. call-heavy code that free-runs for a long time
without the debugger stopping) — pushing past the cap silently drops the
oldest frame rather than growing forever; a debugger call-stack window
losing its very bottom frame during runaway recursion is a fair trade
against unbounded memory.

### New types (`Aemula.Debugging`, shared across all three chip families)

```csharp
public readonly record struct CallStackFrame(
    ushort CallSiteAddress,
    ushort CalledAddress,
    ushort ReturnAddress,
    int StackPointerAtCall);
```

`Debugger` gains:

```csharp
public readonly Stack<CallStackFrame> CallStack = new();
```

`Stack<T>` (not a `List<T>` used as a stack) because its enumeration order
— most-recently-pushed first — is exactly the order a call-stack window
wants to display (innermost frame on top), so the UI can `foreach` it
directly with no reversal.

### Reading the stack pointer: a small callback, mirroring `DebuggerMemoryCallbacks`

`Debugger`'s constructor gains one more parameter:

```csharp
public readonly struct DebuggerStackPointerCallback(Func<int> read)
{
    public readonly Func<int> Read = read;
}
```

(A single-field wrapper rather than a bare `Func<int>` for the same reason
`DebuggerMemoryCallbacks` bundles `Read`/`Write` instead of passing two bare
delegates around — a named type at the call site reads better than an
anonymous `Func<int>` parameter.) `Debugger` gains a matching field,
`public readonly DebuggerStackPointerCallback StackPointer;`, set from the
constructor parameter the same way `MemoryCallbacks` already is. Each
system's `Debugger` subclass passes
one more constructor argument, e.g. `AppleIDebugger`:

```csharp
: base(appleI, CreateMemoryCallbacks(appleI), new(() => appleI.Cpu.SP))
```

NES reads `_nes.Cpu.SP` (the `Ricoh2A03Chip` wrapper already delegates its
own public `SP` straight to the inner `Mos6502Chip` core, the same
precedent `NesDebugger` already follows for `Address`/`Sync`/`RW`) — no
special-casing needed despite the OAM-DMA wrinkle the invalidation plan
had to account for, because DMA never touches the stack.

This is a constructor-time callback rather than a parameter threaded
through `OnAddressExecuting`/`Tick` (the shape the invalidation plan uses
for the bus address) specifically so this plan doesn't need that plan's
`Tick` refactor to exist first — it works against `Debugger` as it stands
today, and keeps working unchanged whether or not that refactor has landed.

### Why RST needs a disassembler fix first, and Intel8080 has a pre-existing bug blocking this entirely

Two problems in the existing disassemblers would silently break call-stack
tracking if not fixed first:

1. **`Intel8080Disassembler.Do2`** always builds `new JumpTarget(JumpType.Jump, operand)`
   when *any* `jumpType` is passed, ignoring the actual value —
   `CALL`/`CZ`/`CNZ`/etc. all come through tagged `Jump`, not `Call`. Every
   Intel8080 call would be invisible to this plan's push logic. One-line
   fix: use `jumpType.Value` instead of the hardcoded `JumpType.Jump`.
2. **`RST n`** (Z80 and Intel8080 both) is currently disassembled via `Do0`
   with `hasNext: false` and no `JumpTarget` at all — it's treated like an
   unconditional jump to nowhere the disassembler can follow, not a call.
   RST is a real, commonly-used subroutine-call form on both chips (fixed-
   address calls to page-zero routines), so needs `JumpTarget(JumpType.Call, target)`
   (target = the RST number's fixed vector — already encoded in each
   `Do0("RST 0x08")`-style call site, just needs threading through as a
   numeric address instead of only text) and `hasNext: true`, so `Next`
   (the return address) is populated the same way every other call site's
   is.

Both are pure disassembler-correctness fixes with no dependency on
anything else in this plan, so they land first as their own phase.

## Phased plan

**Phase 0 — `CallStackFrame`, `DebuggerStackPointerCallback`, `Debugger.CallStack`**

Add the two new types and the `Stack<CallStackFrame> CallStack` field to
`Debugger` per Design, plus the constructor parameter. Wire the push/trim
logic into `OnAddressExecuting`. `Debugger`'s constructor signature change
touches all six system `Debugger` subclasses (Phase 2) — do that in the
same commit as this phase rather than leaving the base class in a
half-wired state, since there's no intermediate state worth preserving
separately.

**Phase 1 — Disassembler fixes: Intel8080 `Do2` bug, RST call classification**

Fix `Intel8080Disassembler.Do2`'s hardcoded `JumpType.Jump` per Design.
Add `JumpTarget(JumpType.Call, ...)` and `hasNext: true` to all eight RST
call sites in both `Z80Disassembler` and `Intel8080Disassembler`. Unit test
in `Aemula.Tests`: for each of the two disassemblers, assert a `CALL`/`CZ`-
style opcode and an `RST` opcode both produce `JumpTarget?.Type == JumpType.Call`
with the correct target address and a non-null `Next`.

**Phase 2 — Wire the stack-pointer callback into all six systems**

Add the `DebuggerStackPointerCallback` argument to each of
`Atari2600Debugger`, `NesDebugger`, `AppleIDebugger`, `AppleIIDebugger`,
`SpaceInvadersDebugger`, `ZX80Debugger`'s `base(...)` calls, reading
`Cpu.SP` (6502-family) or `Cpu.SP.Value` (Z80/8080 — `SPRegister`) through
each system's own already-stored chip reference. No other change to these
files.

**Phase 3 — `CallStackWindow` (`Aemula.UI`)**

New `DebuggerWindow` subclass, added from the base `DebuggerUI.CreateDebuggerWindows`
(not each system's override) so all six systems get it for free, the same
way `DisassemblyWindow` is added today. Renders `debugger.CallStack`
top-to-bottom (innermost first, matching `Stack<T>`'s natural enumeration
order) as a simple list — this is a handful of rows at most, no
`ImGuiListClipper` needed unlike `DisassemblyWindow`'s full 64K-address
sweep. Each row shows the called address (plus `Disassembler.Cache[CalledAddress].Label`
when the disassembler has one — reusing the existing "Subroutine" auto-
label `Disassembler.DisassembleAddresses` already assigns to call targets)
and the return address. An empty stack (at the top level, or between calls)
renders as a small placeholder line rather than an empty window, so it's
visually obvious the window is working rather than broken. No click-to-
navigate in this window in v1 — `DisassemblyWindow` has no goto-address/
scroll-lock mechanism to jump to yet (that's `docs/disassembly-window-plan.md`
Phase 6); once that lands, wiring a frame click to it is a small follow-up,
not attempted here to avoid building against a mechanism that doesn't
exist yet.

**Phase 4 — Manual verification, all six systems**

For each of Atari2600, NES, Apple I, Apple II, Space Invaders, and ZX80:
run something with known nested subroutine calls (any ROM/program with a
couple of levels of `JSR`/`CALL` nesting works), confirm the window shows
the right chain of frames at the right depth, confirm it unwinds correctly
on return, and confirm stepping through a `RTS`/`RET` pops exactly one
frame while a manual test of a multi-level unwind (or, more easily, just
watching what happens across a subroutine that never returns via `RTS` and
instead falls into a longer-running loop) doesn't leave stale frames
sitting forever. For the Z80/8080 systems specifically, exercise an RST-
based call if the running program uses one (common in Space Invaders' ROM)
and confirm it shows up as a frame like any other call, verifying Phase 1's
RST fix actually took effect end-to-end and not just in the unit test.

## Testing

The push/trim logic in `Debugger.OnAddressExecuting` and the RST/Intel8080
disassembler fixes are the pieces with real logic worth testing in
`Aemula.Tests`: a synthetic sequence of fetches (constructed directly
against a `Disassembler`/fake `Debugger`-like harness, not a full running
chip) exercising nested calls, a normal return unwinding one frame, an
unwind past more than one frame in a single step, and the depth cap not
growing unbounded past `MaxDepth`. `CallStackWindow` itself is ImGui
rendering with no meaningful logic boundary — verify manually per Phase 4,
this repo's existing practice for `Aemula.UI` changes.

## Explicitly out of scope

- **Hardware interrupt entry as a visible frame** (6502 BRK/IRQ/NMI, Z80
  NMI/INT, 8080 INT) — each chip models interrupt acknowledgment
  completely differently (6502 hijacks an ordinary fetch cycle rather than
  fetching a real opcode at the vector address; Z80 NMI/IM1 don't fetch a
  real opcode at all, IM2 does a further indirect vector read; 8080 INT has
  the interrupting device jam a byte, normally an RST, directly onto the
  data bus, bypassing memory). Getting this right would mean three
  genuinely different per-chip detection mechanisms (see the chip source
  comments already covering this in `Mos6502Chip.cs`, `Z80Chip.Interrupts.cs`,
  and `Intel8080Chip.cs`'s `StatusWordInterruptAcknowledge`/`WhileHalt`
  constants) rather than the single shared code path the rest of this plan
  gets away with. The SP-driven trim (Design) makes leaving this out safe
  rather than corrupting: an interrupt handler this plan doesn't push a
  frame for simply doesn't appear as one, and its return doesn't wrongly
  pop a real frame either. Worth a dedicated follow-up plan per chip if it
  turns out to matter in practice.
- **6502 `BRK`** as a call-stack entry — same rationale as hardware
  interrupts (it shares the same push-three-bytes-then-vector mechanism as
  IRQ/NMI, "hijacking" the fetch the same way), bundled with the interrupt
  follow-up above rather than solved in isolation here.
- **Click-to-navigate from a call-stack frame to `DisassemblyWindow`** —
  blocked on that window's not-yet-built goto-address/scroll-lock
  mechanism (`docs/disassembly-window-plan.md` Phase 6); trivial follow-up
  once that exists.
- **Reusing `MnemonicCategory.Call`/`Return`** from
  `docs/disassembly-window-plan.md` Phase 2a-2c instead of this plan's own
  `JumpTarget`-based push detection — considered and rejected per Design:
  that categorization is a display concern (does this opcode belong in
  the "Call" color bucket) with no return-side story, whereas this plan
  needs return handling to be SP-driven regardless, so adopting it for the
  call side only would mean depending on an unlanded plan for half of a
  mechanism that doesn't need it for the other half.
- **A frame stack that survives `Disassembler.Reset()`** (media change) —
  a cartridge/tape swap invalidates any in-flight call stack anyway (the
  code that pushed those frames may no longer exist), so `CallStack` is
  simply left to reflect whatever executes next; no explicit clear is
  needed since the SP-driven trim combined with normal execution resuming
  from a reset vector naturally drains stale frames as SP moves. If manual
  verification in Phase 4 finds this looks wrong in practice (e.g. a
  lingering stale frame visibly sitting above a fresh reset until SP
  happens to cross it), revisit with an explicit `CallStack.Clear()` in
  `Disassembler.Reset()`'s caller — not pre-built here since it may not be
  needed.
