# Sinclair ZX80 — Implementation Plan

## Execution notes

When told to execute this plan: work through phases 0–6 (the phases in
scope — see Target configuration for why 7/8 are explicit follow-ups)
autonomously, committing once each phase is done and working rather than
batching everything into one commit or stopping for approval between
phases. Code comments must stand on their own — explain the *why* directly
in the comment, never by citing this document or a phase number, since this
file is deleted once the plan lands (same convention as every other landed
plan in this repo). If anything encountered during implementation calls for
deviating from what's written here — a chip that turns out to behave
differently than described, a wiring assumption the schematic contradicts,
a part that needs different treatment than planned — stop and ask rather
than improvising past it.

## Goal

Add a pin-level ZX80 emulator to Aemula, reusing the existing `Z80Chip`, and
following the project's existing conventions:

- `EmulatedSystem` subclass in `Emulation/Systems/ZX80/ZX80System.cs` (shape
  mirrors `AppleISystem.cs`/`Atari2600System.cs`: a `partial class` split
  into topic files — `.Video.cs`, `.CompositeVideo.cs`, `.Keyboard.cs`,
  `.Cassette.cs` — rather than one monolithic file).
- New chip classes: flat files directly under `Emulation/Chips/` for the
  generic 74xx parts (no companion Debugging/UI files needed), same rule
  the Apple II plan established — a chip only gets its own folder when it
  has companions, which is why `Z80/` has one and e.g. `Ttl7474Chip.cs`
  doesn't.
- ROMs in `Emulation/Systems/ZX80/Roms/`, auto-copied by the existing
  `Content Include="Emulation\**\Roms\*.*"` glob.
- Debugger in `Emulation/Systems/ZX80/Debugging/ZX80Debugger.cs`, reusing
  `Z80Disassembler` (already exists under `Chips/Z80/Debugging/`, used by
  nothing yet — this would be its first consumer since the Z80 chip project
  itself only landed FUSE/zex conformance, not a system).
- Registered in `EmulatedSystems.All` (`Emulation/Systems/EmulatedSystems.cs`)
  as `"zx80"` — this is the current single source of truth for CLI/UI/
  `--input` entry points, superseding the older per-tool `Program.cs`
  dictionary the Apple II plan referred to.
- Per-chip unit tests follow the same flat-vs-folder rule:
  `Aemula.Tests/Emulation/Chips/Ttl74165ChipTests.cs` directly, no subfolder.

## Target configuration

**ZX80, US/NTSC variant, stock 1KB RAM, 4K ROM.**

Per your steer: this is the initial target, not because it's the "default"
ZX80 (the UK PAL board is), but because `Television`/the `Ntsc/` decode
pipeline only implements `TelevisionStandard.Ntsc` today — targeting NTSC
first means the composite output this plan produces is watchable through
the existing decoder with no new decode work, and the PAL variant becomes a
follow-up (new hardware strapping, PAL software decode) once this lands,
not a blocker to getting *anything* on screen.

Concretely, per
[Tynemouth Software's ZX80 USA writeup](http://blog.tynemouthsoftware.co.uk/2022/10/zx80-usa.html),
the US board is the same PCB/BOM as the UK one with a handful of parts
either added or omitted:

- **D11** (1N4148), fitted between the 74LS365 keyboard/cassette buffer
  (IC10) and the 74LS165 video shift register (IC9), pulls data-bus line D6
  low. This is read back through an I/O port read the same way a keyboard
  row is, and — per that writeup — **the same ROM code** branches on it to
  generate a 262-line/60Hz frame instead of 312-line/50Hz, rather than the
  US board shipping different ROM contents. Worth confirming once we have
  the ROM disassembled (phase 3), but if true it means one ROM image serves
  both variants and "US NTSC" is purely this diode plus the two modulator/
  video-buffer differences below, not a separate software build.
- TR1 (video buffer transistor under the heatsink) and D1/D2 are US-only
  discrete analog additions around the RF modulator stage — see the
  "RF modulator" fidelity note below for why these don't need modelling.
- The RF modulator part itself differs (UM1082E3 NTSC vs UM1233 PAL), which
  is exactly the boundary this plan stops at (see below).

Out of scope for this plan, listed as later/stretch phases:
**PAL hardware variant** (the default 50Hz strapping — no D11 — plus the
matching software constant, once we have the ROM's branch confirmed) **and
PAL software decode** (a real `TelevisionStandard.Pal` path through
`Emulation/Output/`) — both explicitly follow-up work per your steer, not
part of this plan. Also out of scope: 16K RAM-pack expansion via the edge
connector, and the "RAM pack wobble" quirk that comes with it.

## Fidelity approach

Per the same spirit as the Apple II plan: **full gate-level fidelity**.
Every 74-series part on the board gets its own pin-level `Chip` class, wired
together in `ZX80System` net-by-net the way the schematic wires them. The
ZX80 is an unusually good fit for this goal — Sinclair's whole design trick
was building the video and I/O subsystem out of *fewer* chips than
contemporaries by working the TTL harder, not by adding a custom video IC,
so "every chip gets modelled" and "almost no arbitrary C#" are the same
goal here, more than on most systems in this repo.

Exceptions, to stay consistent with how the rest of the codebase draws
this line:

- **Bulk storage stays a plain `byte[]`.** The 4K mask ROM (IC2, a 2332) and
  the 1KB of RAM (IC3/IC4, two 2114 1Kx4 SRAMs) are data behind the chips
  that address them, per the established convention (`NesSystem._ram`,
  `AppleIISystem`'s DRAM).
- **The keyboard diode matrix is plain wiring logic, not a chip class.**
  D3–D10 (eight 1N4148s) exist on the board purely to block sneak paths
  between key-matrix rows during a multi-key read — the same job diode
  matrices do on every home computer of this era. Nothing else in this
  codebase models a discrete signal diode as a simulated component (Apple
  II's/Apple I's diode matrices are handled the same way); the row/column
  read-back logic in `ZX80System.Keyboard.cs` is where this lives, wired
  from the real `74LS365` buffer chip, not a bypass of it.
- **The RF modulator is out of scope; feed `Television` pre-modulator.**
  Every system in this repo that has composite output computes it as a
  small analog-summing method close to the chips that drive it (see
  `AppleISystem.CompositeVideo.cs`'s `TickCompositeVideo`) and calls
  `Television.Decode` directly — `Television` already expects a baseband
  composite sample, not an RF-modulated one. The ZX80's UM1233/UM1082
  modulator (and the US-only TR1/D1/D2 buffer stage feeding it) exist only
  to put that same composite signal onto a TV's antenna input, so they're
  the same kind of "stop here" boundary the Apple II plan draws around its
  luma/chroma summing stage — nothing upstream of the modulator is skipped.
- **Monochrome, no color burst.** Like the Apple I, there's no chroma
  subcarrier anywhere on this board — `Television`'s NTSC pipeline decodes
  fine without one (same as `AppleISystem` today), so no new decoder work
  is implied by targeting NTSC first.

## The split data bus and the NOP generator

This is the one genuinely unusual mechanism on the board, and the reason a
"cycle-accurate Z80 core" alone isn't enough — it's worth understanding
before phase 3, since it shapes how `ZX80System` has to be wired.

The ZX80 has **no dedicated video chip**. Instead, it runs the Z80's own
instruction fetch against the display: during the active picture, most
"instructions" the CPU fetches are actually pixel data being smuggled onto
the data bus, forced to *look like* `NOP` (`$00`) so the CPU just idles
one T-state per byte instead of executing garbage. Per
[Tynemouth Software's writeup](http://blog.tynemouthsoftware.co.uk/2019/10/how-the-zx80-works.html):

- The data bus is **physically split into two halves**, joined line-by-line
  through 1K resistors (R1/R3–R12/R18 etc. per the parts list). The Z80,
  the open-collector `74LS05` "NOP generator" bank, and the `74LS365`
  keyboard/cassette buffer sit on one half; the ROM, RAM, and expansion bus
  sit on the other.
- During a video-fetch cycle, the far half of the bus is busy with the
  *real* character/pixel byte (character code out of RAM, looked up through
  `74LS373`, driving the ROM's character-bitmap address, shifted out by
  `74LS165` at the pixel rate). Meanwhile the open-collector `74LS05`s pull
  the CPU-side half of the bus down to `$00` (specifically forcing bit 6
  low, gated by `/HALT`), so the Z80 reads a `NOP` regardless of what the
  far side is doing — the resistors are weak enough that the open-collector
  pulldown wins.
- The Z80's own **refresh cycle** (the `RFSH` pin, and the `I`/`R`-register
  address it puts out during it) is what addresses the character ROM for
  each of the 8 scanlines of a character row — the exact mechanism the
  Tynemouth writeup calls "abuse of the Z80's refresh cycle for
  simultaneous memory access and video generation." This is why `Z80Chip`
  already exposing `Rfsh`, `M1`, `MReq`, `Halt`, `Address` and `Data` as
  real pins (confirmed in the existing code) matters: this trick only works
  if those are genuine pins on our chip, not internal state, and they are.
- At the end of each character row, the ROM `HALT`s the CPU; the
  `74LS93`/`74LS74` horizontal counter chain fires the CPU's `/INT` once per
  scanline, waking it out of `HALT` to run the next line of the row through
  the same fetch-as-NOP mechanism, `RET`urning to `HALT` again. Between
  character rows (the border), the CPU actually executes real BASIC/
  keyboard-scan code — which is *why* the famous ZX80 "flicker" exists:
  real work done during the border visibly steals time from the next
  frame's video generation, and that'll fall out of this model for free
  once the timing chain is right, rather than needing to be special-cased.

Modelling this faithfully means: no shortcut where `ZX80System` "just
renders text" from the display file on some frame boundary. The character
RAM read, the `74LS373` latch, the ROM bitmap fetch, the `74LS165` shift-out,
and the `74LS05` NOP-forcing all have to run every T-state in lockstep with
`Z80Chip.Tick()`, the same granularity `Mos6502Chip`/`Ricoh2C02Chip` are
already ticked at together in `NesSystem`.

## Reference materials

- [ZX80 schematic (redrawn/corrected, KiCad-sourced PDF)](https://github.com/TankedThomas/ZX80/blob/master/Docs/zx80_schematic_kicad.pdf) — primary net-level reference for wiring every chip below.
- [ZX80.kicad_sym](https://github.com/TankedThomas/ZX80/blob/master/ZX80.kicad_sym) and [ZX80_Board.kicad_sch](https://github.com/TankedThomas/ZX80/blob/master/ZX80_Board.kicad_sch) — the underlying KiCad source; the `.kicad_sch` is the actual net-level schematic (the PDF is generated from it) and is plain text, so it's the most reliable source to grep/diff against while wiring each phase.
- [zx80_parts_list.csv](https://github.com/TankedThomas/ZX80/blob/master/Docs/zx80_parts_list.csv) — full BOM with reference designators and per-part fitted/not-fitted notes (this is where the "only for 60Hz" markings on D1/D2/D11/TR1/R26/R30/R31/R33/R36/R37 come from — confirms the NTSC-variant part list above against the primary source, not just the blog post).
- [Tynemouth Software: "How the ZX80 works"](http://blog.tynemouthsoftware.co.uk/2019/10/how-the-zx80-works.html) — the best available prose walkthrough of the split-bus/NOP-generator/refresh-cycle video trick; primary source for the "Split data bus" section above.
- [Tynemouth Software: "ZX80 USA"](http://blog.tynemouthsoftware.co.uk/2022/10/zx80-usa.html) — the US/NTSC variant's hardware differences (D11, TR1, the modulator swap); primary source for the Target configuration section above.
- [Tynemouth Software: "How the ZX81 Generates Video"](http://blog.tynemouthsoftware.co.uk/2023/10/how-the-zx81-generates-video.html) — the ZX81 reuses/refines the same trick; useful cross-check once phase 3 is underway, since it's a second independent writeup of the same class of mechanism.
- [ZX80 character set (Wikipedia)](https://en.wikipedia.org/wiki/ZX80_character_set) — glyph layout for the character ROM, useful for sanity-checking the sourced ROM dump in phase 0.
- Standard 74LS-family datasheets (TI/ON Semi) for each part in the inventory below, plus the Z80 datasheet already used by the existing `Z80Chip`.

## ROMs

Not yet sourced. Needed: the 4K ZX80 ROM image (there are two known
revisions, v1 and v2 — v2 is the later, bug-fixed one and the one almost
every surviving/emulated unit runs; source the v2 dump unless a specific
reason turns up to prefer v1). Given the "same ROM, D11 strap picks 50/60Hz"
claim above, only one image should be needed for both the PAL default and
this plan's NTSC target — confirm during phase 3 once the ROM can be
disassembled/traced, and flag it as a real open risk until then (see below).
Follow the `AppleII/Roms`/`SpaceInvaders/Roms` precedent: a `README.txt`
disclaimer alongside the `.rom` file, sourced from wherever gives the
cleanest known-checksum dump (e.g. the ZXfoss archive or the same TankedThomas/
Tynemouth Software ecosystem this schematic came from).

## Chip inventory (new files under `src/Aemula/Emulation/Chips/`)

Reference designators are from the parts list linked above. Quantities
reflect the default (PAL) BOM; the NTSC-specific additions (D11, TR1, D1/D2)
are called out separately since most are 0-qty in the base BOM.

### Generic 74-series — already in the library, reused as-is

| Part | Function | Designator(s) | Existing file |
|---|---|---|---|
| 74LS00 | Quad 2-input NAND | IC11, IC12 | `Ttl7400Chip.cs` |
| 74LS04 | Hex inverter | IC13 | `Ttl7404Chip.cs` |
| 74LS10 | Triple 3-input NAND | IC16 | `Ttl7410Chip.cs` |
| 74LS32 | Quad 2-input OR | IC17 | `Ttl7432Chip.cs` |
| 74LS74 | Dual D-type flip-flop | IC18, IC19 | `Ttl7474Chip.cs` |
| 74LS86 | Quad 2-input XOR | IC20 | `Ttl7486Chip.cs` |

### Generic 74-series — new (reusable by any future system, not ZX80-specific)

| Part | Function | Designator(s) | Qty | New file |
|---|---|---|---|---|
| 74LS05 | Hex inverter, **open-collector** | IC14, IC15 | 2 | `Ttl7405Chip.cs` |
| 74LS93 | 4-bit binary counter (÷2 and ÷8 sections) | IC21 | 1 | `Ttl7493Chip.cs` |
| 74LS157 | Quad 2-to-1 mux (non-tristate) | IC6, IC7, IC8 | 3 | `Ttl74157Chip.cs` |
| 74LS165 | 8-bit parallel-in/serial-out shift register | IC9 | 1 | `Ttl74165Chip.cs` |
| 74LS365 | Hex buffer, tri-state (4+2 split enable) | IC10 | 1 | `Ttl74365Chip.cs` |
| 74LS373 | Octal transparent latch | IC5 | 1 | `Ttl74373Chip.cs` |

`Ttl7405Chip` needs its own class rather than reuse of `Ttl7404Chip` for the
same reason `Ttl8T97Chip` is separate from a plain buffer: open-collector
outputs need to be modelled as pull/float rather than push-pull, since the
NOP-generator's whole trick depends on that (see above) — it's a genuinely
different electrical behavior, not just a datasheet renumbering.

`Ttl74165Chip` is a new class rather than reuse of the existing
`Ttl74166Chip` — they're both 8-bit PISO shift registers but differ in
control pins (165 has a plain shift/load select and true+complement serial
out; 166 has a separate clock-inhibit and no complement out), so forcing one
class to cover both would mean fidelity-losing compromises on both sides,
the same reasoning that already keeps `Ttl74151Chip`/`Ttl74251Chip` and
`Ttl74153Chip`/`Ttl74157Chip`(new)-shape parts distinct in this library.

### ZX80-specific

| Part | Function | Designator | New file |
|---|---|---|---|
| Z80 | CPU | IC1 | Already have — `Z80/Z80Chip.cs` |
| 2332 | 4K mask ROM | IC2 | `byte[]`, per fidelity note above |
| 2114 | 1Kx4 SRAM ×2 | IC3, IC4 | `byte[]`, per fidelity note above |

No keyboard-encoder chip exists on this board (unlike the Apple II's
AY-5-3600) — key sensing is a plain diode matrix read back through the
`74LS365` buffer, so there's no new chip class needed for it, only wiring
logic in `ZX80System.Keyboard.cs` per the fidelity note above.

### US/NTSC-specific additions (0-qty in the base/PAL BOM)

| Part | Function | Designator | Notes |
|---|---|---|---|
| 1N4148 | NTSC strap diode | D11 | Wired per the "Target configuration" section — pulls D6 low, read back as an I/O port bit. No new chip class; same non-modelled-diode treatment as the keyboard matrix. |
| ZTX329/ZTX238 | Video buffer transistor | TR1 | Feeds the US modulator; out of scope per the "RF modulator" fidelity note — the pre-buffer composite signal is what `Television.Decode` receives. |
| BA220/BA221 (sub: 1N4148) | — | D1, D2 | Part of the same US-only analog stage as TR1; same out-of-scope treatment. |

## Phased plan

**Phase 0 — Scaffolding and ROM sourcing**
Source and verify the 4K ROM (see above), set up `ZX80System` skeleton,
register `"zx80"` in `EmulatedSystems.All`, empty
`Debugging/ZX80Debugger.cs`. Nothing functional yet.

**Phase 1 — Generic 74xx chip library**
Implement and unit-test every "new" chip in the tables above (`Ttl7405Chip`,
`Ttl7493Chip`, `Ttl74157Chip`, `Ttl74165Chip`, `Ttl74365Chip`,
`Ttl74373Chip`), with real datasheet-driven tests (truth tables, shift/count
sequences), independent of the ZX80 board — same spirit as Apple II's
phase 1.

**Phase 2 — CPU/memory core, no video**
Wire `Z80Chip` + the 1KB RAM (behind IC3/IC4) + the 4K ROM (behind IC2),
using the real `74LS00`/`74LS10`/`74LS32` gating for address decode
(`A14`/`A15`-driven ROM-vs-RAM-vs-expansion select — confirm exact gate
network against the schematic). No display, no keyboard, no video-fetch
trick yet — this phase's `ZX80System.Tick()` should already be T-state-
granular (`Z80Chip.Tick()` once per system tick), since phase 3 depends on
that granularity being right from the start rather than retrofitted.
**Done when:** the CPU runs the reset vector and the debugger's
register/memory view shows sane execution (there's no video yet to confirm
it visually).

**Phase 3 — Video timing chain + the NOP generator**
Build the `74LS93`/`74LS74` horizontal counter chain (HSYNC generation, the
per-scanline `/INT`), wire the split data bus (`74LS05` open-collector NOP
force, gated by `/HALT` and bit 6), and the character-fetch path
(`74LS373` latch → ROM bitmap address → `74LS165` shift-out) off the Z80's
`RFSH`/`Address`/`Data` pins, per the "Split data bus" section above. This
is the phase where the "one ROM, D11 picks 50/60Hz" claim gets confirmed or
corrected against the actual disassembly. **Done when:** HSYNC pulses and
the video shift-out bit stream look right on a scope-style trace (debugger
signal view), even before composite summing exists.

**Phase 4 — Composite video output + keyboard**
Wire the analog-summing stage (a `TickCompositeVideo`-shaped method, like
`AppleISystem`'s, combining the shift register's serial video bit with the
HSYNC/VSYNC signals) into `Television.Decode`. Wire the keyboard matrix
(diode-matrix reads through `74LS365`) to `OnKeyEvent`. **Done when:** you
can see and type at the `K`/BASIC prompt through `TelevisionWindow`.

**Phase 5 — Cassette I/O**
Wire the `74LS365`'s cassette-EAR input and the VSYNC-derived MIC/tape-out
path (per the writeup: VSYNC low-pass-filtered to strip the 15kHz HSYNC
component, leaving the tape modulation) to a `CassetteDeck`-shaped
peripheral via `PeripheralRequests`, matching `AppleISystem`'s ACI
precedent (transport controls on the status bar, `MediaBay` for the tape
image). **Done when:** a known-good ZX80 program `.wav`/tape image
LOADs and runs.

**Phase 6 — NTSC validation**
With D11 wired in per the Target configuration section, confirm the frame
comes out at 262 lines/60Hz through the existing `Television` NTSC
pipeline with no decoder changes needed. This is the plan's overall
**done** milestone.

**Phase 6 addendum — in progress, boot screen still not working.** Two real
bugs found and fixed (uncommitted) while chasing "why is the screen blank":

1. **D11 was never actually wired.** `ZX80System.Keyboard.cs`'s
   `ReadKeyboardMatrix` forced D6 high unconditionally (`0x60 | ear |
   columns`) with a comment noting D6 "carries the NTSC strap diode" but
   never actually driving it low. Per the Target configuration section, D11
   should pull D6 low on every keyboard-style read, unconditionally (it
   isn't gated by row/column like a real key - the diode sits on the data
   line itself). Fixed to `0x20 | ear | columns`. Confirmed with
   `--trace-timing`: before the fix, frame geometry drifted from an initial
   262 lines up toward ~310 lines (the PAL default the ROM falls back to
   without the strap) and never stabilized; after the fix it converges
   cleanly to 262.00 lines/frame, matching the NTSC target. This is phase
   6's own validation goal and now passes.
2. **The character latch (IC5) was reading the post-NOP-force byte.**
   `DoCpuMemoryAccess` (`ZX80System.cs`) set `Cpu.Data` from ROM/RAM, then -
   still within the same call - `TickNopGenerator` could overwrite it to
   `0x00`. `TickVideo` ran afterward and latched `Cpu.Data` into the
   character code (`ZX80System.Video.cs`), so on every cycle where the NOP
   generator actually forced (i.e. almost every real character fetch), the
   latch saw the forced `0x00` instead of the real character code - the
   screen would only ever have been able to show character 0 (space).
   Fixed by capturing the byte into a new `_realDataBus` field before
   `TickNopGenerator` runs, and reading `_realDataBus` (not `Cpu.Data`) into
   the character latch. See the fidelity note below - this fix is a patch
   on an existing shortcut, not a true fix of the underlying modeling gap.

**Still broken: no content ever renders.** Confirmed with
`Aemula.Console --frames 3000` (~326M ticks, ~25 emulated seconds - far
longer than any plausible RAM-check delay) that the screen stays blank
throughout; `Television`'s decoded sample buffer never contains anything
but sync tip and one paper/ink level (paper), i.e. the video shift
register's output bit (`QhN`) never toggles from its idle state. Direct
instrumentation (reflection over `_videoShiftRegister`'s `ShLd` property)
showed the shift register's load strobe fires **once** across a 30,000,000-
tick run - it should fire on the order of thousands of times (once per
character cell). Whatever combination of `phiRisingEdge` /
`notLatchedNopDecode` (`_characterLatch.Q6`, the latched output of
`GetNopDecodeSignal`) / `Cpu.MReq` is supposed to produce that strobe in
`TickVideo`'s `shLd` calculation essentially never lands.

This is *not* simply "needs more boot time before the first frame":
instrumented tracing of CPU registers at every entry to the ROM's `$0038`
interrupt handler shows the hardware side of the per-scanline HALT/INT
handshake looking basically sane at coarse grain - the ROM's own routine
there (confirmed against the ROM image: `0D C2 45 00 E1 05 C8 CB D9 ED 4F
FB E9`, i.e. `DEC C / JP NZ,$0045 / POP HL / DEC B / RET Z / SET 3,C / LD
R,A / EI / JP (HL)`, the well-documented ZX80 scanline-interrupt routine)
runs with `B`/`C` counting down and `HL` correctly advancing by one display-
file byte (`0xC02B` -> `0xC02C`) roughly once per real HSYNC line period
(~828 ticks between successive `$0038` entries, matching
`DetectedSamplesPerLine`). So the coarse row/line bookkeeping the interrupt
handler drives isn't obviously broken, yet actual per-character video
generation (the shift-register load inside a display-file M1, which should
be happening far more often than this once-per-line interrupt) never
kicks in. The disconnect between "line-level bookkeeping looks right" and
"character-level shift-register loading essentially never happens" is
where the next session should start - likely by instrumenting every M1
during one of these traced `$0038`-to-`$0038` intervals to see what
`Cpu.Rfsh`/`Cpu.Address`/`shLd`'s three inputs actually do tick-by-tick,
since hand-deriving it from the gate equations alone (attempted at length
this session) did not converge on a definitive answer.

**Fidelity gap worth addressing before debugging further.** The split data
bus - the plan's own "one genuinely unusual mechanism" - is not actually
modeled as two physically distinct bus halves joined by resistors, the way
the plan's "Split data bus" section describes it. It's one shared
`Cpu.Data` field, and the NOP generator (`IC14`/`IC15`) is modeled as a
single conditional `Cpu.Data = 0x00` in `DoCpuMemoryAccess` rather than
eight independent open-collector gates each pulling their own bit low (a
shortcut phase 3 already took deliberately - see `ZX80System.Video.cs`'s
`_ic15` field comment: "the real board's IC14 has one gate... folded into a
single `Cpu.Data = 0x00`... since which of eight near-identical gates
forces which bit makes no observable difference"). The `_realDataBus` fix
above is built on top of that same shortcut - it's a shadow copy of
"`Cpu.Data` before the force," not a real second bus. Functionally this
should be equivalent (the far side of the real split bus is always exactly
"what ROM/RAM/keyboard buffer just drove, before any near-side pulldown"),
but it means this codebase is not modeling the split bus with real chip
objects the way the rest of the plan's fidelity approach calls for, and
that shortcut - or a bug in how `_realDataBus` interacts with it - is a
reasonable suspect for the remaining rendering bug. Worth considering
whether to model the NOP-forcing bank as real per-bit open-collector gates
(this codebase already has the pattern for tri-state modeling, e.g.
`Ttl8T97Chip`/`Ttl74251Chip`'s nullable-bool tri-state outputs) with two
genuine bus-half byte values joined through the actual resistors, rather
than continuing to patch the single-field shortcut.

**Fidelity gap addressed (uncommitted).** `IC14` now exists as a real
`Ttl7405Chip` (it wasn't instantiated at all before), and the NOP-forcing
bank is modeled as eight real open-collector gates - `IC14`'s six plus two
of `IC15`'s previously-spare gates - each driven from the same force-enable
signal and recombined per-bit against `_farDataBus` (renamed from
`_realDataBus`, now a genuine second bus-half rather than a shadow copy) in
a new `CombineSplitDataBus` method. `GetNopDecodeSignal`'s D6 test and the
video shift register's parallel-load inputs were switched from `Cpu.Data`
to `_farDataBus` too, since both are real far-side (`D0'`-`D7'`) reads on
the schematic, and reading the post-force `Cpu.Data` (as `GetNopDecodeSignal`
was doing when called from `TickVideo`) is circular. Building the eight
gates surfaced a real polarity bug: they were first wired with the gates'
input tied straight to the OR-gate `release` signal, but `Ttl7405Chip`'s
inverter convention means a gate only pulls its output low when its input
is *true* - so this fed the pulldown backwards (forcing bits to 0 exactly
when the circuit should *release* them, and floating - i.e. leaving them
alone - exactly when it should force). That regressed 5 of 10
`--treenode-filter "/*/*/ZX80*/*"` tests, including the basic
`RunsResetVectorAndInitializesStackPointer` CPU-boot test having nothing to
do with video, because the forcing bank runs on every M1 fetch system-wide,
not just during video generation - confirmed against the pre-change tree
(all 10 passing there) before tracking it down. Fixed by feeding the gates
`!release` instead. All 10 ZX80 tests pass again after the fix.

This also produced a real change in emulated output: `Aemula.Console --frames
<n> --screenshot` at several frame counts (30/75/120/210/333) now shows a
small non-blank mark that changes between two distinct states depending on
frame count (consistent with a blinking cursor), where before this fix the
addendum above found the decoded frame buffer held nothing but sync tip and
one paper level for 3000 frames straight. This is a strong sign the
shift-register load strobe is now actually firing, though the visible mark
is a small cursor-shaped artifact near the bottom-left corner rather than a
recognizable "K" boot prompt - worth checking next whether that position/
scale is a display-model bug (e.g. a vertical flip) or the shift register is
still only loading intermittently.

**Re-instrumented the `ShLd`-strobe count (throwaway reflection-based test,
not committed) - found and fixed the actual rendering bug.** The count was
still 1 falling edge across 30,000,000 ticks, unchanged from the original
addendum finding above - the split-bus fidelity fix was real and worth
doing, but it wasn't this bug. Tracing `_characterLatch`'s internal `_le`/
`_d6`/`_q6` fields tick-by-tick around a genuine character fetch (address
`0xC043`, far bus `0xB0` - a `K` cursor glyph with the inverse-video flag
set) found the actual cause: a one-tick-late latch-enable ordering bug in
`TickVideo`, not the split bus at all. `Ttl74373Chip`'s `Le` was assigned
*after* `D0`-`D7` each tick. On the tick a display-file M1's refresh half
begins (every M1 cycle, `Le` correctly falls to end the transparent
window), the `D6` setter ran first and still read the *previous* tick's
stale `_le = true`, so it performed one more transparent capture using this
tick's now-irrelevant refresh-phase `nopDecode` (computed against the
refresh address, where A15 is always clear) - overwriting the correct
character-fetch value `Le`'s own falling edge was about to freeze. Fixed by
moving `_characterLatch.Le = le;` to before the `D0`-`D7` assignments, so
the D setters see the current tick's `Le` state: a falling-edge tick now
freezes immediately (D setters see the already-updated `false` and no-op),
and a rising-edge tick is still correctly transparent (the D setters see
the already-updated `true` and capture fresh values right after `Le`'s own
edge-triggered bulk copy, which used stale D inputs, runs). This took the
`ShLd` falling-edge count from 1 to 3,073 over the same 30,000,000 ticks -
in the "thousands, roughly once per character cell" range the original
addendum expected. All ZX80 tests still pass (10/10), and
`Aemula.Console --screenshot` now renders a solid, glyph-shaped black block
(the inverse-video cursor, by its position and shape) in place of the
earlier one-pixel artifact - not yet the full "K" prompt text, and its
on-screen position (bottom-left rather than top-left) is still unexplained,
worth checking next along with whether the remaining gap is a display/
timing offset or something further in the shift-out/composite-video path.

**Phase 7 (stretch, explicit follow-up per your steer) — PAL variant**
Add the 50Hz strapping (no D11, the default BOM) as a build/config option
on `ZX80System`, and a genuine `TelevisionStandard.Pal` decode path under
`Emulation/Output/` — real new decoder work, not just a flag flip, since
`Television` only implements NTSC today.

**Phase 8 (stretch) — 16K RAM pack**
Edge-connector RAM expansion (R19 disables the on-board 2114s when an
external pack is fitted) — not attempted here; the real hardware's
"RAM pack wobble" (a loose edge connector losing contact) is a physical
defect, not something meaningful to emulate, so this phase is just the
extra RAM and decode, not the wobble bug.

## Open risks

- **The "one ROM, D11 picks 50/60Hz" claim is sourced from a blog post, not
  yet from the disassembly.** It's specific and plausible enough to plan
  around, but confirm it in phase 3 once the ROM can actually be traced —
  if it turns out the US board also shipped different ROM contents, phase 0
  needs a second ROM image and phase 6 needs to pick the right one.
- **Exact gate-level wiring (which 74LS00/10/32 gate does which decode, exact
  74LS157 mux channel assignments) needs confirming against the schematic
  per phase**, same caveat the Apple II plan carries — the parts list and
  blog description above establish *what* each chip does, not the exact pin-
  to-pin netlist, which is what the `.kicad_sch`/PDF are for during
  implementation.
- **T-state-granular co-simulation of `Z80Chip` and the video chain is more
  demanding than the Apple II's bus-sharing model** — the Apple II
  alternates CPU/video bus access on a coarser, more regular schedule; the
  ZX80's video *is* the CPU's instruction fetch stream for most of the
  frame, so a subtle off-by-one-T-state bug is more likely to show up as
  visibly wrong character rendering than as a silent timing drift. Budget
  real debugging time for phase 3, not just wiring — similar in spirit to
  the Apple II plan's AY-5-3600 caveat, but sharper here since it's the
  system's core mechanism, not one peripheral.
- **Flicker/border-stealing-time behavior is an emergent property of getting
  phase 3 right, not a feature to build separately** — if it doesn't show
  up naturally once the timing chain and `HALT`/`/INT` handshake are
  correct, that's a signal something upstream is wrong, worth treating as a
  correctness check rather than a nice-to-have.
