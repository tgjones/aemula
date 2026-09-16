# Disassembly window aesthetics & functionality — Implementation Plan

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

`DisassemblyWindow` (`src/Aemula.UI/DisassemblyWindow.cs`) works but is
visually flat — every line is the same weight, colors are three hardcoded
constants (gray labels, red breakpoint dot, yellow PC triangle), and it
shows only what's needed to identify a line, not what's actually known
about it. Because every chip in this repo is emulated at the
transistor/pin level, the debugger already has (or can cheaply gain)
information a typical disassembler can't offer without symbolic execution:
exactly how many cycles an instruction actually took, exactly how often an
address has executed, and exactly what an indexed/indirect operand
resolves to right now. This plan makes the window read better at a glance
(syntax highlighting, row tinting, theme-consistent colors) and surfaces
more of that live information, plus a few interaction improvements that
piggyback on debugger functionality (breakpoints, watchpoints) that
already exists but isn't reachable from this window today.

Explicitly not part of this plan (raised and cut in discussion): illegal/
undocumented opcode flagging, and inline label/comment editing.

## Design

### Foundation: move row rendering onto an ImGui table

Today each row is drawn with manual `ImGui.SameLine(250)` offsets inside a
plain child window. That works for plain text, but per-row background
tinting — which several phases below depend on — is a `ImGui.TableSetBgColor`
call, which only exists inside an `ImGui.BeginTable`/`TableNextRow` region.
`LogicAnalyzerWindow` already establishes the pattern this repo uses for
theme-consistent color (`ImGui.GetColorU32(ImGuiCol.Text/TextDisabled/
TableHeaderBg/...)` rather than hardcoded hex), and `TableSetBgColor` is how
it tints rows. Phase 0 below converts `DisassemblyWindow`'s row loop to a
table with explicit columns (gutter, address, bytes, mnemonic, operand,
annotation) so row tinting and per-column color/dimming both become
straightforward, and stays a pure layout change — the `_disassembly` line
list and its contents are untouched. `ImGuiListClipper` keeps working
unmodified inside a table (same pattern: `clipper.Step()` outside,
`TableNextRow`/`TableNextColumn` per visible row inside).

### Live instrumentation: three independent per-address arrays fed by hooks that already fire every instruction

`Debugger.OnAddressExecuting` (called from each chip's debugger subclass on
every instruction fetch, not just the first time an address is seen) and
`Disassembler.OnAddressExecuting` are the two places that already see every
single instruction fetch. Three features below (execution-count heatmap,
last-execution cycle count, and — indirectly — the "run to cursor" feature)
are all just "record something small here, render it later":

- **Execution counts**: `Disassembler.OnAddressExecuting` currently early-
  returns once an address is already disassembled. Split the counter
  increment out so it happens unconditionally on every call, into a new
  `int[0x10000]` array on the base `Disassembler` class (so it's free for
  every chip family, not just Mos6502).
- **Last-execution cycle count**: needs a running tick counter, which
  doesn't exist yet. Add `Debugger.TotalTicks` (incremented once per loop
  iteration in `RunForDuration`, alongside the existing `Ticked` event).
  `Debugger.OnAddressExecuting` then records, per address, the tick delta
  between this fetch and the previous one — i.e. "how many cycles did the
  *previous* instruction actually take," measured rather than looked up in
  a static per-opcode table. That measurement is correct for branches,
  page-crossing penalties, and interrupts for free, which a static table
  wouldn't be.

Both arrays reset (`Array.Clear`) wherever `Disassembler.Reset()` already
resets the disassembly cache (media change), so stale counts from a
previous cartridge don't linger.

### Effective-address resolution needs live register state, which the disassembler doesn't have today

For indexed/indirect addressing modes (`LDA ($28),Y` and friends), the
debugger actually knows what address and byte that resolves to *right
now* — but only for the instruction currently at PC; for any other row
in view it's a hypothetical (X/Y could be anything next time that line
runs), so this only makes sense rendered on the current-PC row, recomputed
live every frame. `Mos6502Disassembler` currently only has
`DebuggerMemoryCallbacks` and an equates table — no register access. Add a
new virtual hook on the base `Disassembler`:

```csharp
public virtual (ushort Address, byte Value)? TryGetEffectiveAddress(in DisassembledInstruction instruction) => null;
```

`Mos6502Disassembler` overrides it, needing X/Y read access threaded into
its constructor the same way `DebuggerMemoryCallbacks` is already threaded
in (a small callback bundle, not a direct `Mos6502Chip` reference — keeps
`Disassembler` decoupled from concrete chip types, matching the existing
design). Scoped to Mos6502 only for v1 (see Explicitly out of scope).

### Mnemonic/operand coloring is baked in at construction, not reverse-engineered from the formatted string

Rejected approach: split `Disassembly` at render time (first space for
mnemonic/operand, character-sniffing `#`/`(`/`,X` for operand sub-parts).
That works, but it means the window is re-deriving information the
disassembler already had and threw away when it flattened everything into
one string. Instead, `DisassembledInstruction` carries the categorization
as data, produced once at the point each chip's disassembler already knows
it:

```csharp
public readonly record struct DisassembledInstruction(
    ushort Opcode,
    ushort AddressNumeric,
    string Address,
    byte InstructionSizeInBytes,
    string RawBytes,
    string Mnemonic,
    MnemonicCategory MnemonicCategory,
    string Operand,
    OperandKind OperandKind,
    ushort? Next,
    JumpTarget? JumpTarget)
{
    // Convenience for existing plain-text consumers (Mos6502ChipTestHelper,
    // Ricoh2A03ChipTestHelper, and DisassemblyWindow's own copy-to-clipboard
    // action) — computed on demand, not stored.
    public string Disassembly => Operand.Length > 0 ? $"{Mnemonic} {Operand}" : Mnemonic;
}
```

`MnemonicCategory` (Branch, Call, Return, LoadStore, Arithmetic, Logic,
Stack, Transfer, FlagOp, IO, Other) and `OperandKind` (None, Immediate,
Address, Indirect, Register) live in `Aemula.Debugging` alongside
`DisassembledInstruction`, shared across all three chip families. The
window then just does `TextColored(categoryColor, instruction.Mnemonic)`
and `TextColored(operandKindColor, instruction.Operand)` — no parsing, no
string-splitting, no character sniffing.

This is a bigger change than the string-splitting approach because the
three chip disassemblers get there very differently today, and each needs
touching:

- **Mos6502** (`Aemula.CodeGen/Mos6502CodeGenerator.cs`) is the easy case:
  it already has a per-opcode `Instruction` record (mnemonic string +
  `AddressingMode` + `MemoryAccess`) known at codegen-authoring time, and
  `AddressingModeDescriptions` already maps `AddressingMode` to prefix/
  suffix/`IsAddress`. Add a mnemonic→`MnemonicCategory` static lookup in
  the *generator itself* (not emitted per-instruction logic — the category
  is a compile-time-known constant per opcode, so the generator bakes the
  literal enum value directly into the generated `switch`), and derive
  `OperandKind` from `AddressingMode` the same way. `Mnemonic` becomes a
  literal string reference (already effectively free — opcode mnemonics
  are a small fixed set of interned literals), and `Operand` is the
  prefix/hex/equate/suffix formatting that already exists today, just no
  longer including the mnemonic itself.
- **Z80** (`Z80Disassembler.cs`) and **Intel8080** (`Intel8080Disassembler.cs`)
  are hand-written opcode switches where `Do0`/`Do1`/`Do2` helpers
  currently bake the *entire* line (mnemonic and operand together) into
  one template string per call site, e.g. `Do2("LD (0x", suffix: "), HL")`.
  There's no existing structured split to build on here — every call site
  across the base, CB, ED, DD, and FD tables needs its mnemonic and
  operand template separated, plus a category tag. This is mechanical but
  large in surface area (hundreds of opcode entries between the two
  chips), so it's split into its own phase per chip below rather than
  bundled with Mos6502.

### Where `Span<char>` genuinely helps: composing `Operand` without intermediate allocations

Today's operand formatting (e.g. `$" {prefix}{(equates.TryGetValue(...) ? equate : "$" + operand.ToString("X2"))}{suffix}"`)
allocates a throwaway string for `operand.ToString("X2")`, another for the
`"$" + ...` concatenation, then the final interpolated string — several
small allocations to produce one short string, once per disassembled
instruction. Since `Mnemonic` becomes a literal (no allocation) once it's
split out, `Operand` is the one piece worth building efficiently: compose
it with `string.Create(length, state, static (span, state) => ...)`,
writing the prefix, then either the equate's characters or the hex digits
via `operand.TryFormat(span, out _, "X2")`, then the suffix, directly into
the destination buffer — one allocation instead of three-plus. Apply this
in whichever of the three disassemblers it falls out naturally (Mos6502's
shared formatting helper is the obvious first candidate); it's a
performance nicety on top of the structural change, not a requirement for
the coloring to work, so don't force it where a chip's existing formatting
doesn't lend itself to it cleanly.

## Phased plan

**Phase 0 — Table-based row layout**

Convert `DisassemblyWindow`'s row rendering from manual `SameLine` offsets
to `ImGui.BeginTable` with columns for gutter (breakpoint dot / PC
triangle), address, bytes, mnemonic, operand, and a trailing annotation
column (used by later phases for cycle counts and effective-address
notes). Purely structural — verify the listing looks the same, just
columnar, before building anything on top of it.

**Phase 1 — Row highlighting + theme-consistent dimming**

- PC row: `ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ...)` with a
  translucent tint, in addition to (not instead of) the existing yellow
  triangle marker — the triangle stays the precise indicator, the tint
  makes the row easy to spot while scrolling past it.
- Breakpoint row: a faint red row tint, on top of the existing gutter dot.
  Decide and document the precedence when a row is both the PC and a
  breakpoint (e.g. PC tint wins, breakpoint dot still shows in the gutter).
- Bytes column rendered with `ImGuiCol.TextDisabled` instead of full-
  strength text — it's the lowest-signal column today.
- Replace the hardcoded `0x99` gray constant used for labels/separators/
  ellipsis with `ImGui.GetColorU32(ImGuiCol.TextDisabled)`, matching
  `LogicAnalyzerWindow`'s existing theme-relative convention instead of a
  fourth ad-hoc palette.

**Phase 2a — Structured `DisassembledInstruction` + Mos6502**

Add `MnemonicCategory`/`OperandKind` enums and restructure
`DisassembledInstruction` per the Design section (`Mnemonic`/
`MnemonicCategory`/`Operand`/`OperandKind` replacing the flat
`Disassembly` field, with `Disassembly` kept as a computed convenience
property so `Mos6502ChipTestHelper`/`Ricoh2A03ChipTestHelper` need no
changes). Update `Mos6502CodeGenerator.cs` to emit the category/kind and
split mnemonic/operand as described. `DisassemblyWindow` renders
`Mnemonic`/`Operand` with `TextColored` using each's category/kind color —
no parsing. Fixed small accent colors for categories ImGui has no
built-in role for (same precedent as `TelevisionWindow`'s hand-picked
signal-overlay palette).

**Phase 2b — Z80**

Rework `Z80Disassembler`'s `Do0`/`Do1`/`Do2` helpers to take a mnemonic
and category separately from the operand template, and update every call
site across the base, CB, ED, DD, and FD tables. Large in surface area but
mechanical; the FUSE/zexdoc/zexall test suite isn't affected since this
only touches disassembly text construction, not execution, but re-run it
after this phase as a sanity check that nothing adjacent got disturbed.

**Phase 2c — Intel8080**

Same rework as 2b, applied to `Intel8080Disassembler`'s equivalent
helpers and opcode table.

**Phase 3 — Execution-count heatmap**

Add the unconditional per-address execution-count array to `Disassembler`
(see Design). Render as a subtle background tint scaled by count (log
scale, clamped), layered with the Phase 1 row tints — heat as the base
layer, PC/breakpoint tints drawn over it at higher alpha so a breakpoint
on a hot loop line still reads clearly as a breakpoint. Clear the array
alongside the existing cache clear in `Disassembler.Reset()`.

**Phase 4 — Last-execution cycle count**

Add `Debugger.TotalTicks` and the per-address last-execution-cycles array
(see Design). Render as small dim right-aligned text in the annotation
column (e.g. `4c`), shown only once an address has actually executed —
sparse is expected and fine.

**Phase 5 — Effective-address / resolved-value annotation (Mos6502 only)**

Add `Disassembler.TryGetEffectiveAddress` (see Design) and implement it in
`Mos6502Disassembler` for ZeroPageX/Y, AbsoluteX/Y, IndexedIndirectX,
IndirectIndexedY, and Indirect addressing modes, threading live X/Y read
access into its constructor. Rendered only on the current-PC row, as
trailing dim text, e.g. `LDA ($28),Y   ; ($1A3F) = $07`. Other chips'
`Disassembler` subclasses inherit the base no-op (`null`) for now.

**Phase 6 — Context menu, run-to-cursor, goto-address, scroll lock**

- Right-click on an instruction row (`ImGui.BeginPopupContextItem`, keyed
  by address like the existing `ImGui.PushID(instruction.AddressNumeric)`)
  offering: toggle execution breakpoint (existing
  `BreakpointManager.ToggleExecutionBreakpoint`), add byte/word watchpoint
  here (existing `AddValueByteBreakpoint`/`AddValueWordBreakpoint` —
  currently only reachable via `BreakpointsWindow`; this exposes them
  contextually from the disassembly view), run to cursor, copy
  address, copy bytes.
- Run to cursor: new `Debugger.RunToAddress(ushort address)` sets a
  one-shot target; `RunForDuration`'s existing "did PC change this tick"
  check gains a check against that target (cleared once hit) alongside
  the normal `Breakpoints.ShouldBreak` check — no permanent breakpoint
  left behind, no new `BreakpointManager` entry needed.
- Goto-address: a small input box + button above the listing, reusing
  (factored into a shared helper rather than duplicated) the existing
  `indexToScrollTo`/`SetScrollY` auto-scroll logic.
- Scroll-lock toggle: gates the existing PC auto-scroll so it doesn't yank
  the view back while manually browsing after a goto-address jump — the
  two features would otherwise fight each other.

**Phase 7 — Breakpoint/PC scrollbar markers (investigate first)**

ImGui has no native scrollbar-decoration API, so unlike the other phases
this one starts with a feasibility spike: either an overlay strip drawn
alongside the child window using index-to-scroll-fraction math, or a
fallback to a compact in-window breakpoint list/strip if that proves
fiddly. Decide the approach before implementing; this phase is the most
speculative item in the plan and can be descoped without affecting
anything else here.

**Phase 8 — Manual verification**

Launch the UI against a system with a known hot loop, confirm: table
layout matches the old visual density; PC/breakpoint/heat tinting all read
clearly, individually and combined; syntax highlighting looks right across
a range of mnemonics and addressing modes; cycle counts and
effective-address annotations are sane (hand-verify a couple of
instructions); context menu actions all work; run-to-cursor stops in the
right place; goto-address and scroll-lock don't fight the PC auto-scroll.

## Testing

The three per-address arrays (execution counts, last-execution cycles),
the effective-address computation, and the new mnemonic/operand
categorization are the pieces here with real logic worth unit testing —
put those in `Aemula.Tests`, which already references `Aemula.csproj`.
The categorization in particular is worth a real assertion per phase: for
each chip, assert every opcode's `DisassembledInstruction.Mnemonic` is
non-empty and its `MnemonicCategory`/`OperandKind` aren't left at a
forgotten default — cheap insurance against the mechanical Z80/Intel8080
table rewrites (2b/2c) silently mis-tagging or skipping an entry across
hundreds of call sites. Everything else (table layout, colors, popups,
scroll behavior) is ImGui rendering with no meaningful boundary to unit
test; verify those manually per Phase 8, per this repo's existing practice
for `Aemula.UI` changes.

## Explicitly out of scope

- Illegal/undocumented opcode flagging, and inline label/comment editing —
  both raised during design discussion and explicitly cut.
- Data-vs-code classification and self-modifying-code cache invalidation
  (the existing `Disassembler.OnDataWritten` TODO) — unrelated to this
  plan's aesthetics/functionality goal; the execution-count array in
  Phase 3 is adjacent live instrumentation but doesn't require solving
  this.
- Effective-address resolution for chips other than Mos6502 — revisit once
  the pattern proves valuable there; the other chip disassemblers simply
  inherit the base no-op.
- A true scrollbar minimap, if Phase 7's spike finds it impractical in
  ImGui — falls back to the simpler in-window strip described there.
