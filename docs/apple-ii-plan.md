# Apple II — Implementation Plan

## Goal

Add a pin-level Apple II emulator to Aemula, reusing the existing `Mos6502Chip`,
and following the project's existing conventions:

- `EmulatedSystem` subclass in `Emulation/Systems/AppleII/AppleIISystem.cs`
  (shape mirrors `NesSystem.cs` / `SpaceInvadersSystem.cs`).
- New chip classes: a chip gets its own `Emulation/Chips/<ChipName>/` folder
  only when it has companion files (Debugging/, UI/, multiple partial
  classes) — same as `Mos6502`, `Ricoh2C02`, etc. today. The generic 74xx
  parts and the keyboard encoder have no companions, so they're flat files
  directly under `Emulation/Chips/`, e.g. `Emulation/Chips/Ttl74283Chip.cs`.
- ROMs in `Emulation/Systems/AppleII/Roms/` — **already in place**
  (`Apple2_Plus.rom`, `Apple2_Video.rom`), auto-copied by the existing
  `Content Include="Emulation\**\Roms\*.*"` glob in `Aemula.csproj`.
- Debugger in `Emulation/Systems/AppleII/Debugging/AppleIIDebugger.cs`, reusing
  `Mos6502Disassembler`.
- Registered in `Aemula.UI/Program.cs`'s `Systems` dictionary as `"appleii"`.
- Per-chip unit tests follow the same flat-vs-folder rule:
  `Aemula.Tests/Emulation/Chips/Ttl74283ChipTests.cs` directly, no subfolder,
  since these don't need helper/state companion files the way
  `Mos6502ChipTests.cs` does.

## Target configuration

**Apple II+, NTSC, 48K RAM, Autostart Monitor + Applesoft BASIC in ROM.**

This is the best-documented revision — schematics and ROM dumps are the most
available, and it's a strict superset of the original Apple II's hardware
(only the ROM contents and the Integer BASIC vs. Applesoft split differ, so
original Apple II support falls out later as a ROM swap, not new hardware).

Out of scope for the initial build, listed as later/stretch phases below:
Disk II controller, Language Card / bank-switched RAM, cassette I/O.

## Fidelity approach

Per your call: **full gate-level fidelity.** Every 74-series part on the
board gets its own pin-level `Chip` class, wired together in `AppleIISystem`
net-by-net the way the schematic wires them — the same spirit as
`Mos6502Chip`/`Ricoh2C02Chip`, extended to the glue logic instead of
special-casing it in C#.

Two exceptions, to stay consistent with how the rest of the codebase already
draws this line:

- **Bulk storage stays a plain `byte[]`.** Nothing in this repo models RAM/ROM
  ICs as chip objects (see `NesSystem._ram`, `SpaceInvadersSystem._rom`) —
  only the logic that *addresses* them is simulated. The 48K DRAM array, the
  ROM images, the character-generator ROM, and the video-scrambling PROM are
  all `byte[]` behind the chips that drive their address/data lines.
- **DRAM refresh is a side effect, not a simulated 4116.** The 4116 chips'
  only interesting behavior (periodic RAS refresh) is inherent to the video
  scan's periodic memory access, which the video-timing chips *do*
  reproduce cycle-by-cycle. We don't need a stateful "DRAM chip" to get that
  right, so RAM itself is just the `byte[]`.

## Reference materials

- [Apple II Schematics (II & II+)](https://downloads.reactivemicro.com/Apple%20II%20Items/Hardware/II_%26_II%2B/Schematic/Apple%20II%20Schematics.pdf) — primary net-level reference for wiring every chip below.
- [Schematic Diagram of the Apple II+](https://mirrors.apple2.org.za/ftp.apple.asimov.net/documentation/hardware/schematics/Schematic%20Diagram%20of%20the%20Apple%20II+.pdf) — alternate/cleaner scan of the same.
- [The Apple II Circuit Description](http://www.apple-iigs.info/doc/fichiers/TheappleIIcircuitdescription1.pdf) — prose walkthrough of the board, useful alongside the schematic.
- *Understanding the Apple II* by Jim Sather — the definitive technical reference for the video-timing state machine and memory design; worth having a copy for phases 3–5.
- [AY-5-3600 datasheet](http://www.applelogic.org/files/AY3600.pdf) — keyboard encoder.
- [Apple II motherboard chip list (Applefritter)](https://www.applefritter.com/content/apple-ii-motherboard-chips) — the parts inventory phase 1's table below is based on; confirm exact designators/quantities against the schematic per phase, since they drift slightly across board revisions.
- Standard 74LS-family datasheets (TI/ON Semi) for each part in the inventory below.

## ROMs

Done — sourced from
[AppleWin's `resource` folder](https://github.com/AppleWin/AppleWin/tree/3e8054b4627624398e4589f7f27b3d40a6b9718e/resource)
and placed in `Emulation/Systems/AppleII/Roms/`, with a disclaimer
`README.txt` matching the precedent set by `SpaceInvaders/Roms` and
`BbcMicro/Roms`:

- `Apple2_Plus.rom` (12,288 bytes) — Autostart Monitor + Applesoft BASIC,
  pre-joined into one image mapped at `$D000`–`$FFFF`. On real hardware this
  is six separate 2K ROM ICs (sockets D0/D8/E0/E8/F0/F8); the combined image
  is exactly the six concatenated, so a single flat `byte[0x3000]` behind the
  ROM-select decode logic is all we need.
- `Apple2_Video.rom` (2,048 bytes) — character generator dump (stands in for
  the Signetics 2513 / Apple 341-0036), used by the video shift-out logic in
  phase 4.

Sizes both check out against the real hardware layout, and a spot-check of
the bytes looks like plausible 6502 code / glyph data rather than garbage —
should be good to load as-is.

*(Stretch, phase 8)* A DOS 3.3 or ProDOS boot disk image will be needed once
we get to the Disk II controller — not sourced yet.

## Chip inventory (new files under `src/Aemula/Emulation/Chips/`)

Quantities are from the Applefritter board survey — confirm against the
schematic during implementation, since revisions vary slightly.

### Generic 74-series (reusable by any future system, not Apple II-specific)

| Part | Function | Qty on board | New file |
|---|---|---|---|
| 74LS161 | 4-bit sync binary counter | 4 | `Ttl74161Chip.cs` |
| 74LS138 | 3-to-8 decoder | 4 | `Ttl74138Chip.cs` |
| 74LS139 | Dual 2-to-4 decoder | 1 | `Ttl74139Chip.cs` |
| 74LS151 | 8-to-1 mux | 1 | `Ttl74151Chip.cs` |
| 74LS153 | Dual 4-to-1 mux | 4 | `Ttl74153Chip.cs` |
| 74LS257 | Quad 2-to-1 mux, tri-state | 5 | `Ttl74257Chip.cs` |
| 74LS194 | 4-bit bidirectional shift register | 3 | `Ttl74194Chip.cs` |
| 74LS259 | 8-bit addressable latch | 1 | `Ttl74259Chip.cs` |
| 74LS174 | Hex D flip-flop | 2 | `Ttl74174Chip.cs` |
| 74S175 | Quad D flip-flop | 1 | `Ttl74175Chip.cs` |
| 74LS251 | 8-to-1 mux, tri-state | 1 | `Ttl74251Chip.cs` |
| 74LS74 | Dual D flip-flop | 3 | `Ttl7474Chip.cs` |
| 74166 | 8-bit parallel-in/serial-out shift register | 1 | `Ttl74166Chip.cs` |
| 74LS283 | 4-bit binary adder | 1 | `Ttl74283Chip.cs` |
| 74LS00 | Quad 2-input NAND | 1 | `Ttl7400Chip.cs` |
| 74LS02 | Quad 2-input NOR | 4 | `Ttl7402Chip.cs` |
| 74LS04 | Hex inverter | 1 | `Ttl7404Chip.cs` |
| 74LS08 | Quad 2-input AND | 2 | `Ttl7408Chip.cs` |
| 74LS11 | Triple 3-input AND | 1 | `Ttl7411Chip.cs` |
| 74LS20 | Dual 4-input NAND | 1 | `Ttl7420Chip.cs` |
| 74LS32 | Quad 2-input OR | 1 | `Ttl7432Chip.cs` |
| 74S86 | Quad 2-input XOR | 1 | `Ttl7486Chip.cs` |
| 8T97 | Tri-state hex buffer (or 74LS367/74F367) | 3 | `Ttl8T97Chip.cs` |
| 555 | Timer (astable/monostable) | 2 | `Ne555Chip.cs` |
| 741 | Op-amp (cassette in/out analog stage) | 1 | *deferred — see below* |

The 741 op-amp is analog (cassette input comparator); it's on the board but
not needed until the cassette-I/O stretch phase, so it's not in the initial
build.

### Apple II-specific

| Part | Function | New file |
|---|---|---|
| AY-5-3600 | Keyboard matrix encoder | `Ay53600Chip.cs` |
| 6502 | CPU | Already have — `Mos6502/Mos6502Chip.cs` |

Video-scrambling PROM and character-generator ROM are data (`byte[]`), not
chip classes, per the fidelity note above.

## Future goal: analog composite video into `Television`

You asked whether the plan already covers what's needed for color burst.
**Mostly yes, with two things worth locking in now rather than retrofitting
later:**

1. **Master clock granularity.** The plan now calls for `AppleIISystem` to
   tick at the full 14.31818MHz crystal rate (added to phase 2 above), not
   at CPU-cycle granularity. Since the CPU clock, dot clock, and 3.579545MHz
   color subcarrier (crystal ÷ 4) are all synchronous divisions of that one
   oscillator, ticking at the master rate is what keeps the subcarrier phase
   available at all — there's no separate "color clock" chip to add, it's a
   free byproduct of clocking everything from the one master signal, but
   only if we don't shortcut the granularity.
2. **A named color-burst-gate output.** Phase 3 now calls this out
   explicitly rather than lumping it in with HBL/VBL, since it's exactly the
   signal a composite encoder needs as an input later.

**What's still net-new work when you get to the `Television` feature** (not
part of this plan's chip inventory, since it isn't a 74-series part):

- The actual luma/chroma **summing/encoder stage** — on real Apple II
  hardware this is a small discrete analog circuit (resistor network +
  transistor buffer combining video data, sync, and burst into one composite
  waveform), not a digital chip. It'll want to live in the system/output
  layer (or a new small class near `Television`), computed as a weighted sum
  driven by the digital signals phases 3–5 already produce (video bit
  stream, HBL/VBL, color-burst gate, subcarrier phase) — genuinely new work,
  but small, and everything it needs as input already exists once phases 3–5
  are done.
- `Television`/`Oscillator` in `Emulation/Output/Television.cs` are current
  unimplemented stubs (`NotImplementedException`) — that class will need
  actual implementation whenever this becomes active work, separate from
  this plan.

## Phased plan

**Phase 0 — Scaffolding**
Set up `AppleIISystem` skeleton, `Program.cs` registration (`"appleii"`),
empty `Debugging/AppleIIDebugger.cs`. ROMs are already in place. Nothing
functional yet.

**Phase 1 — Generic 74xx chip library**
Implement and unit-test every chip in the "Generic 74-series" table (minus
the 741). These are useful beyond Apple II, so they get real datasheet-driven
tests (truth tables, counter sequences) in `Aemula.Tests`, independent of the
Apple II board.

**Phase 2 — CPU/memory core, no video**
Wire `Mos6502Chip` + 48K RAM (`byte[]` behind the `74LS257`/`74LS153` address
muxes) + `Apple2_Plus.rom`, using the real `74LS138`/`74LS139` decoders for
RAM/ROM/I-O select. Soft-switch space (`$C000`–`$CFFF`) can initially
read/write as open bus. **`AppleIISystem.Tick()` should step at the full
14.31818MHz master oscillator rate**, not at CPU-cycle granularity — like
`NesSystem` ticking its PPU dot clock and deriving the CPU clock from it.
Everything on this board (CPU clock, pixel/dot clock, color subcarrier) is a
synchronous division of that one crystal, so preserving that phase
relationship from the start is what makes the future analog video output
(see below) possible without a rewrite. **Done when:** the CPU runs the reset
vector and executes instructions correctly (verified via the debugger's
memory/register view and an instruction trace — there's no video yet).

**Phase 3 — Video timing chain**
Build the horizontal/vertical counter chain (`74LS161` ×4, `74LS174`/`74S175`/
`74LS74` flip-flops) that generates HBL, VBL, and — as a named, distinct
output, not folded into the others — the **color burst gate**: the signal
that's high for ~9 cycles per scanline during the back porch, telling a
future composite encoder when to output the reference subcarrier burst. This
same counter chain also produces the CPU/video bus-sharing alternation that
makes the 6502 effectively run at ~1.023MHz despite the 14.318MHz crystal —
and, on real hardware, does so via a non-uniform division (mostly ÷13 with
one stretched "long cycle" every 65 cycles) to keep the dot clock
long-term phase-locked to the color subcarrier across scanlines. Get this
cycle-accurate now; it's the same mechanism that keeps color-burst phase
continuous, so getting it wrong here means fixing it twice.

**Phase 4 — Text video + keyboard**
Wire the video-address-scrambling PROM, character-generator ROM, and
`74166` shift register to produce 40-column text into a `DisplayBuffer`
(same mechanism `SpaceInvadersSystem` already uses). Wire the `AY-5-3600`
keyboard encoder to the `$C000`/`$C010` soft switches. **Done when:** you can
see and type at the Applesoft/Monitor prompt.

**Phase 5 — Lo-res & Hi-res graphics**
Mode soft switches (`$C050`–`$C057`), lo-res color block generation, hi-res
mode's extra shift/color logic and page-2 addressing.

**Phase 6 — Speaker + game I/O**
Speaker toggle latch, annunciators, paddle timer (one of the `555`s as an
R-C one-shot) for the 4 analog paddle inputs, pushbutton reads.

**Phase 7 (stretch) — Language Card / 64K bank switching**

**Phase 8 (stretch) — Disk II controller**
P6 state-machine PROM, stepper-motor phase logic, needs a DOS 3.3/ProDOS
disk image to test against.

## Open risks

- Exact chip designators/interconnects need confirming against the schematic
  per phase — the Applefritter list is a real board survey but not
  guaranteed identical to every II+ revision.
- DRAM-refresh-via-video-scan timing is subtle; if it looks wrong, cross-check
  against a known-good reference implementation (AppleWin, MAME) rather than
  re-deriving from the schematic alone.
- AY-5-3600 debounce/strobe/rollover behavior is its own small pin-level
  project — budget real time for phase 4, not just wiring.
