# Disassembly cache invalidation on data writes — Implementation Plan

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

`Disassembler.OnDataWritten(ushort address)` (`src/Aemula/Debugging/
Disassembler.cs`) exists but does nothing (`// TODO: Invalidate cache for
this address.`), and nothing calls it anywhere in the codebase today. Once
an address is disassembled it stays cached forever, even if the bytes
underneath it change — so any RAM write that lands on already-disassembled
code (self-modifying code, or a loader writing a program into RAM after
some other code already ran and got cached there) leaves stale, wrong text
in `DisassemblyWindow` with no way to refresh short of a full
`Disassembler.Reset()`.

This surfaced while discussing the ZX80: loading a tape runs the ROM's
real loader routine, writing the loaded program into RAM via ordinary CPU
write cycles — indistinguishable, from the disassembler's point of view,
from any other RAM write during execution. So this isn't a tape-specific
feature; it's a pre-existing general gap. All six systems with a
`Debugger` (`Atari2600`, `Nes`, `AppleI`, `AppleII`, `SpaceInvaders`,
`ZX80`) are in scope, not just ZX80.

Each system's debugger currently hand-rolls its own copy of "detect an
instruction fetch, call `OnAddressExecuting`" in a `TickSystem` override —
five nearly-identical copies for the four Mos6502-based systems plus
`SpaceInvadersDebugger`'s Intel8080 version, and the Z80 version added to
`ZX80Debugger` earlier in this session. Adding a write-detection check to
all six would mean six *more* near-duplicate blocks. Since the detection
logic is entirely a property of which CPU a system uses, not of the
system itself, this plan also consolidates it: each CPU family's existing
debugger helper class gains one method that knows that chip's fetch/write
signals, and every system's `TickSystem` override shrinks to a one-line
call into it.

## Design

### Making `OnDataWritten` actually invalidate

A write can land on the exact address a cached instruction starts at, or
on the second/third/fourth byte of an instruction cached at an *earlier*
address — so invalidation has to look backward, not just clear the exact
address:

```csharp
private const int MaxInstructionSizeInBytes = 4; // covers every chip's disassembler today (6502/8080: 3, Z80 DD/FD CB: 4)

public void OnDataWritten(ushort address)
{
    for (var i = 0; i < MaxInstructionSizeInBytes && i <= address; i++)
    {
        var candidateAddress = (ushort)(address - i);
        ref var candidate = ref Cache[candidateAddress];

        if (candidate.Instruction is { } instruction &&
            candidateAddress + instruction.InstructionSizeInBytes > address)
        {
            candidate.Instruction = null;
            candidate.Label = null;
        }
    }

    Changed = true;
}
```

The `i <= address` guard avoids wrapping below `0x0000` into the top of
the address space on a write to a low address. Clearing `Label` alongside
`Instruction` is consistent with how labels already get (re)assigned:
they're only ever set during `DisassembleAddresses`'s walk (either the
`Reset()` walk or the single-address lazy walk `OnAddressExecuting`
triggers), so if this address is later re-executed, relabeling happens
the same way it would for any other never-before-seen address. Setting
`Changed = true` is required — without it, `DisassemblyWindow` won't know
to rebuild its line list and the stale text stays on screen even though
the cache is now correct underneath.

This lives on the base `Disassembler` class, so every chip family gets it
for free once something calls it.

### Consolidating fetch/write detection into the per-chip debugger helpers

Two chip families already have a small helper class alongside their chip
(`Mos6502Debugger`, `Intel8080Debugger` — both in `.../Debugging/`,
constructed once per system and currently used only for
`RegisterStepModes`). Z80 doesn't have one yet; `ZX80Debugger` has been
doing the fetch-edge detection itself since the earlier fix in this
session. Give each of the three a `Tick` method that owns *all* of that
chip's signal knowledge — which pins mean "fetch happened" and "write
happened," any edge-detection state that requires, and the `FinishedReset`
gating — and takes the current bus address as a parameter rather than
reading it off its own stored chip reference:

```csharp
// Mos6502Debugger
public void Tick(Debugger debugger, ushort busAddress)
{
    if (!Cpu.FinishedReset)
    {
        return;
    }

    if (Cpu.Sync)
    {
        debugger.OnAddressExecuting(busAddress);
    }
    else if (!Cpu.RW)
    {
        debugger.Disassembler.OnDataWritten(busAddress);
    }
}
```

```csharp
// Intel8080Debugger
public void Tick(Debugger debugger, ushort busAddress)
{
    if (!_cpu.Sync)
    {
        return;
    }

    switch (_cpu.Data)
    {
        case Intel8080Chip.StatusWordFetch:
            debugger.OnAddressExecuting(busAddress);
            break;
        case Intel8080Chip.StatusWordMemoryWrite:
        case Intel8080Chip.StatusWordStackWrite:
            debugger.Disassembler.OnDataWritten(busAddress);
            break;
    }
}
```

```csharp
// new Z80Debugger (src/Aemula/Emulation/Chips/Z80/Debugging/Z80Debugger.cs)
public sealed class Z80Debugger
{
    public readonly Z80Chip Cpu;

    private bool _previousM1 = true;

    public Z80Debugger(Z80Chip cpu) => Cpu = cpu;

    public void Tick(Debugger debugger, ushort busAddress)
    {
        // /M1 stays low for the whole 3-T opcode-fetch machine cycle; only its
        // falling edge marks the start of a new fetch - a level check would
        // report every T-state it stays low as its own fetch.
        var m1 = Cpu.M1;
        if (!m1 && _previousM1)
        {
            debugger.OnAddressExecuting(busAddress);
        }
        _previousM1 = m1;

        if (!Cpu.Wr)
        {
            debugger.Disassembler.OnDataWritten(busAddress);
        }
    }
}
```

Each system's `TickSystem` override collapses to one line, e.g.
`AppleIDebugger`:

```csharp
protected override void TickSystem()
{
    base.TickSystem();

    _mos6502Debugger.Tick(this, _appleI.Cpu.Address);
}
```

**Why the address is a parameter, not read from `Cpu.Address` inside the
helper**: for every system except NES this is the same value either way.
NES is the exception — `Ricoh2A03Chip.Address` is `_address ??
_cpuCore.Address` (and `RW` similarly `_rw ?? _cpuCore.RW`), a DMA-driven
override on top of the inner Mos6502 core's own address, and
`NesDebugger`'s existing (already pin-verified) code deliberately reads
`_nes.Cpu.Address` — the wrapper, not `_nes.Cpu.CpuCore.Address`. `Sync`
and `RW` don't need the same treatment: OAM DMA only ever *reads* CPU RAM
(to feed PPU OAM), never writes it, so it can't be the source of a
self-modifying-code write, and the inner core's own `Sync`/`RW` are fine
to read directly off the stored `Cpu` reference. Keeping the address as a
`Tick` parameter means `Mos6502Debugger` doesn't need to know any of this
— each system just keeps passing whatever address expression it already
uses today, and `NesDebugger` is the one line that looks different
(`_nes.Cpu.Address` instead of `_nes.Cpu.CpuCore.Address`, which is
exactly what it already does now).

**Accessibility**: `debugger.OnAddressExecuting` is called instead of
`debugger.Disassembler.OnAddressExecuting` directly because it also
updates `Debugger.LastPC` (which drives the PC marker, auto-scroll, and
breakpoint evaluation) — skipping it would silently break all three. It's
currently `protected`, callable only from within `Debugger` or a
subclass; since these chip-debugger helper classes live in the same
`Aemula` assembly but aren't `Debugger` subclasses, it needs to widen to
`internal`. `Disassembler.OnDataWritten` is already `public` and
`Debugger.Disassembler` already a public field, so no change is needed
for the write side.

**A pre-existing inconsistency this consolidation resolves**:
`Atari2600Debugger`'s current fetch check is `if (_system.Cpu.Sync)` —
missing the `&& Cpu.FinishedReset` that `AppleIDebugger`, `AppleIIDebugger`
and `NesDebugger` all have. Folding Atari2600 into the shared
`Mos6502Debugger.Tick` makes it gain that gate too (it's the majority
behavior and the semantically correct one — nothing should be tracked as
"executing" during the CPU's own internal reset sequence, before its
address bus is meaningful). This is a small, deliberate behavior change,
not a preserved quirk.

## Phased plan

**Phase 0 — Implement `OnDataWritten`**

Add the invalidation loop and `Changed = true` to `Disassembler.
OnDataWritten`, as above. Unit tests in `Aemula.Tests` (pure logic, no
ImGui): exact-address clear, multi-byte-overlap clear (an instruction
cached one/two/three bytes earlier whose range covers the written
address), no clear when nothing cached overlaps, no wraparound when
writing near `0x0000`, and `Label` cleared alongside `Instruction`.

**Phase 1 — Widen `Debugger.OnAddressExecuting` to `internal`**

One-line accessibility change, no behavior change. Confirm the five
existing same-assembly call sites (the system debuggers, still calling it
unqualified from within their own `TickSystem` overrides at this point)
still compile unchanged.

**Phase 2 — `Mos6502Debugger.Tick` and its four systems**

Add `Tick` to `Mos6502Debugger` per Design. Update `Atari2600Debugger`,
`NesDebugger`, `AppleIDebugger`, and `AppleIIDebugger`'s `TickSystem`
overrides to the one-line delegating form, each passing the same address
expression they use today (`NesDebugger` keeps passing `_nes.Cpu.Address`,
not `_nes.Cpu.CpuCore.Address`). Note in the commit that Atari2600 gains
the `FinishedReset` gate it was missing, per Design.

**Phase 3 — `Intel8080Debugger.Tick` and SpaceInvaders**

Add `Tick` to `Intel8080Debugger` per Design (fetch branch behaviorally
identical to today; write branch is new). Update `SpaceInvadersDebugger.
TickSystem` to the one-line delegating form.

**Phase 4 — `Z80Debugger` and ZX80**

New `Z80Debugger` class per Design, moving the `_previousM1` edge-detection
state and fetch check out of `ZX80Debugger` (added earlier this session)
and adding the new write check alongside it. Update `ZX80Debugger.
TickSystem` to the one-line delegating form; `ZX80Debugger` keeps its
`_system` field (still needed to pass `_system.Cpu.Address` into `Tick`)
but loses the `_previousM1` field, which now lives on `Z80Debugger`.

**Phase 5 — Manual verification, all six systems**

For each of Atari2600, NES, Apple I, Apple II, Space Invaders, and ZX80:
confirm disassembly still populates as code runs, the PC marker/auto-scroll
and existing breakpoints still behave exactly as before (this phase must
not regress any of the five systems that worked before this plan), and
step modes still work (`RegisterStepModes` is untouched by this plan, but
verify the consolidation didn't disturb their shared `Cpu`/`_startPC`
state). Then confirm write invalidation: overwrite some already-executed,
already-cached code (a short self-modifying test snippet is the most
portable trigger across all six systems today) and confirm the window's
text updates instead of showing stale disassembly. Re-verify the ZX80
case specifically against a real tape load once `docs/
zx80-tape-loading-plan.md` lands, if this phase runs before that plan
does.

## Explicitly out of scope

- Registering Z80 step modes ("Step Instruction" / "Step CPU Cycle") for
  ZX80 — `ZX80Debugger` currently shows only Break/Continue with no step
  modes at all, and `Z80Debugger` (new in Phase 4) would be the natural
  home for a `RegisterStepModes` method mirroring `Mos6502Debugger`'s and
  `Intel8080Debugger`'s, but it's a separate, unrelated gap. Easy
  follow-up once `Z80Debugger` exists.
- Precise ROM/RAM chip-select replication or I/O-vs-memory-write
  disambiguation in any of the three write checks — a write cycle that
  lands outside real RAM, or (on Z80) an I/O write sharing `/WR` with an
  I/O port number on the low address bus, might invalidate a cache slot
  that didn't actually change. Harmless everywhere: worst case is an
  address gets lazily re-disassembled from unchanged bytes next time it
  executes, producing the identical result. Not worth threading each
  system's private chip-select logic out to avoid it.
- Modeling NES OAM DMA any more precisely than "it only reads CPU RAM, so
  the write path never needs to see it" (see Design) — if that
  understanding turns out to be wrong, revisit, but it's not re-derived
  from scratch here beyond what's needed to justify not special-casing it.
