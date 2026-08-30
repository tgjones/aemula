# NES analog NTSC composite video — Implementation Plan

## Goal

Make `NesSystem` produce a real analog NTSC composite-video signal and feed it,
one sample at a time, into a `Television` — exactly the way `AppleIISystem`,
`Atari2600System` and `SpaceInvadersSystem` already do. When this lands:

- `NesSystem` implements `IHasTelevision`; `((IHasTelevision)nes).Television`
  returns a live-decoding `Television`.
- `nes` is registered in `Aemula.Console/SystemRegistry.cs`, so
  `aemula-console nes --rom …/nestest.nes --screenshot out.png` produces a PNG
  of what a TV would show.
- Enough of the 2C02 render pipeline exists to draw `nestest.nes`'s background
  text screen (no sprites, no scrolling — see scope below).

The NES is the good case for this project's "encode the real analog waveform"
approach: **the 2C02 itself generates the composite waveform** — sync, blanking,
colour burst and an 8-level luma/phase DAC on a single pin — so there is no
encoder circuit to model on the board. We reproduce the DAC + phase logic
behaviourally in the chip, then do only a trivial level-scaling + resample step
in a `NesSystem.CompositeVideo.cs` partial.

## How the 2C02 makes video (reference model)

Sources: NESDev Wiki *"NTSC video"* and *"PPU palettes"*; lidnariq's die-level
signal-level measurements linked from that page; Bisqwit's NES palette
generator (the widely-mirrored `wave()` / `levels[8]` formulation); the
`FlawlessChips` transistor-level `Flawless2C02` (already a test dependency) as
the bit-exact oracle for the digital `vid_*` pins.

### The video pins

`Flawless2C02` exposes the 2C02's internal video nodes, and the existing
prototype (`src/Aemula/Emulation/Systems/Nes/Ppu/Ricoh2C02.cs`) already names
them:

- `vid_sync_l` / `vid_sync_h` — sync tip and blanking level
- `vid_burst_l` / `vid_burst_h` — the colour-burst square wave (rides on blanking)
- `vid_luma{0..3}_l` / `vid_luma{0..3}_h` — the four brightness levels, each a
  low/high pair the chip toggles between at the chroma phase
- `vid_emph` — emphasis/attenuation gate (see "Out of scope")

Exactly one pair is "selected" at any instant, and within a pixel the output
alternates between that pair's `_l` and `_h` member according to the chroma
square wave's phase. That is the entire DAC.

### Levels

The prototype's `VOut` getter already carries a transcribed set of DAC codes
(`vid_sync_l`≈48, `vid_sync_h`≈312, `vid_burst_l`≈148, `vid_burst_h`≈524,
`vid_luma0_h`≈616, `vid_luma3_l`≈880, `vid_luma3_h`≈1100, …). **Completing this
table is a task** — fill in the remaining `vid_luma{0..3}_{l,h}` entries from
NESDev's NTSC-video level table / lidnariq's measurements, keep the whole set on
one arbitrary unit scale, then map that scale onto the shared composite-video
byte scale every other producer emits:

- sync tip → **0**, blanking → **64**, reference white → **224**
  (`NtscYiqDecoder` reconstructs gain from sync↔blanking, so these three points
  must be exact; everything else rides the same linear map, as in
  `Atari2600System.CompositeVideo.cs`).
- The NES legitimately runs some luma+chroma peaks above 224 — let them clamp at
  255, same call `AppleIISystem.CompositeVideo.cs` documents for its hot white.

Special hue codes (NESDev): `$x0` = constant `_h` (grey, no chroma), `$xD` =
constant `_l` (dark grey), `$xE`/`$xF` = blanking level (black). Luma code
`$0x..$3x` picks which `vid_luma{n}` pair.

### Phase / hue

Chroma is a square wave at f_SC with 12 discrete phase positions per cycle. The
canonical model:

```
highForThisSubsample = ((hue + phase) % 12) < 6      // hue 1..12; 0/13 handled above
```

where `phase` is a running position in twelfths of a subcarrier cycle. Because
341 dots/line is not a multiple of 3, `phase` at the start of each line advances
120°, so the dot↔subcarrier relationship repeats every **3 scanlines**; the
odd-frame short pre-render line (`Ricoh2C02Chip.cs:207` TODO) shifts it again,
giving the real per-frame chroma alternation. In hardware this is a Johnson
counter clocked at ½ the master clock — matching the prototype's
`_colorGeneratorClockCounter` running 0..11.

### Clocks — mapping master/6 to the Television's 4× f_SC

| Clock | Rate | Note |
|---|---|---|
| Master | 21.477272 MHz | `NesSystem.CyclesPerSecond`, one per `Tick()` |
| Colour subcarrier f_SC | 3.579545 MHz | master / 6 |
| PPU dot | 5.369318 MHz | master / 4 → **1 dot = ⅔ f_SC = 8/12 of a subcarrier cycle** |
| **Chroma phase grid** | **42.95 MHz** | **12 × f_SC = 2 × master** — the native grid the `wave()` formula lives on |
| **Television sample rate** | **14.31818 MHz** | **4 × f_SC** — what `Television.Decode` requires |

So the signal is naturally defined at **12 × f_SC** and the Television wants
**4 × f_SC** — an exact **3:1 decimation**. The 2C02 output is piecewise-constant
on the 12× grid, so a 4× sample spanning exactly three 12× cells *is* the exact
time-average of those three cells — **box-average groups of 3, no windowing
approximation involved**. (Picking 1-of-3 instead would alias the square wave's
above-2×f_SC energy.)

Concretely: each `Tick()` is one master clock = **two** 12×-grid cells. Keep a
`mod 3` counter across the two-cells-per-tick stream; every third cell, push
`(byte)(accumulator / 3)` to `Television.Decode`. Over a line: 341 dots × 8
cells = 2728 cells → 909.3 Television samples, matching
`NtscTiming.NominalSamplesPerLine`. Cell boundaries do not align to dot
boundaries (8 not divisible by 3) — correct and automatic as long as the 12×
cell stream is generated continuously, independent of dot boundaries.

## What exists vs. what's missing

Present:
- `Ricoh2A03Chip` — CPU, already validated against `nestest.log`.
- `Ricoh2C02Chip` — register file ($2000–$2007), palette RAM + mirroring,
  VRAM read/write handshake over the multiplexed address/data pins, VBlank flag,
  NMI, dot/scanline counters. **No pixel pipeline, no video pin.**
- `NesSystem` — master-clock `Tick()` fanning out to CPU (÷12) and PPU (÷4),
  cartridge load (NROM/mapper 0 only), CPU/PPU bus wiring.
- `Ppu/Ricoh2C02.cs` — an orphaned prototype of the *video timing* state machine
  (HPos-driven sync/burst transitions, 12-state colour counter, level DAC). Not
  wired to anything. Fold the useful parts into the real chip and delete it.
- `src/Aemula.Tests/…/Nes/Ricoh2C02Tests.cs` — skipped, but already stands up
  `Flawless2C02` alongside the prototype and compares `vid_*` node-by-node.
- `Television` + full NTSC decode pipeline — consumes 4× f_SC byte samples,
  self-calibrates sync/gain/geometry, colour-kills burst-less lines.
- `Aemula.Console` — `FrameRunner` (frame = `Television.CurrentRow` wrap; no NES
  change needed), `ScreenshotWriter`, `--screenshot[-every]`.

Missing (this plan):
1. 2C02 background render pipeline (background only).
2. 2C02 video-signal generation (level DAC + 12-phase chroma + sync/blank/burst
   + vblank equalizing/serration pulses), on a new output.
3. `NesSystem.CompositeVideo.cs` — scale to the byte scale, 3:1 box-decimate,
   `Television.Decode`; `NesSystem : IHasTelevision`.
4. `SystemRegistry` + console/test asset wiring for `nestest.nes`.

## Design decisions

### Behavioural model in `Ricoh2C02Chip`, not gate-level at runtime

Extend the real chip class with the NESDev-documented level table + 12-phase
math, driving sync/burst/blank from the dot/scanline counters it already keeps.
This matches the repo's established split — `TiaChip` generates its own
colour-burst and levels behaviourally, and the *system* file does the analog
sum. `Flawless2C02` is **too slow for runtime** (a transistor solve per
half-cycle) and would drag `FlawlessChips` into the main assembly; it stays a
**test oracle** only (revive `Ricoh2C02Tests`).

### Where the analog step lives

`NesSystem.CompositeVideo.cs`, mirroring the Apple II / Atari files: a
`Television` property, a compile-time level map, the 3:1 box decimator, and the
per-`Tick` call into `Television.Decode`. The chip emits a small struct / codes;
the system turns it into bytes. Keeps "chip = behaviour, system = analog sum"
intact.

### Decimation: box-of-3 (exact for this signal), upgradeable

As argued above, box-of-3 is the exact area resample of a piecewise-constant
12× signal to 4×. If a later comparison against `Flawless2C02` traces shows
residual chroma error, the box can become a short decimating FIR without
touching callers.

## File changes

### `src/Aemula/Emulation/Chips/Ricoh2C02/Ricoh2C02Chip.cs` (+ new partials)

- **`Ricoh2C02Chip.Render.cs`** — background pipeline (see next section).
- **`Ricoh2C02Chip.Video.cs`** — the video-signal state machine:
  - Per dot: from the pipeline's 6-bit output colour (`LLHH`) or, outside active
    video, from sync/blank/burst state, select the DAC tap set.
  - A 12-state phase counter advanced 8 states/dot (2 per master clock), giving
    the `_l`/`_h` choice each 12× cell via the `((hue+phase)%12)<6` rule.
  - Horizontal sync / breezeway / colour burst (9 cycles) / back porch / front
    porch from the dot counter; vertical equalizing + serrated vsync pulses
    across the vblank lines. The prototype's HPos transition table
    (`Ppu/Ricoh2C02.cs`) is the starting point; validate against `Flawless2C02`.
  - Odd-frame dot skip on the pre-render line (fills the existing
    `Ricoh2C02Chip.cs:207` TODO) so chroma phase is frame-coherent.
  - Output: expose the current 12×-cell value as a DAC code (arbitrary units)
    via a method/field the system reads twice per `Tick()`.
- Delete `src/Aemula/Emulation/Systems/Nes/Ppu/Ricoh2C02.cs` and its
  `namespace Aemula.Emulation.Systems.Nes.Ppu`.

### New: `src/Aemula/Emulation/Systems/Nes/NesSystem.CompositeVideo.cs`

`partial class NesSystem : IHasTelevision`:

- `public Television Television { get; } = new();`
- `const byte SyncLevel = 0, BlankingLevel = 64, WhiteLevel = 224;` + a
  `static readonly` map from the chip's DAC-code scale to bytes, applied once
  into a small lookup keyed by (tap, l/h).
- `_decimatePhase` (`0..2`) + `_decimateAccumulator`; on each of the two 12×
  cells per `Tick()`, add the mapped byte, and every third cell emit
  `Television.Decode((byte)(_decimateAccumulator / 3))` and reset.
- `CurrentCompositeVideoSample` for parity with the other systems' scope channels.

### Changed: `src/Aemula/Emulation/Systems/Nes/NesSystem.cs`

- `class NesSystem : EmulatedSystem, IHasTelevision` (via the partial).
- In `Tick()`, after the PPU cycle, call the composite-video pump.
- `LoadProgram` already does `Cartridge.FromFile`; nothing to change for
  `nestest.nes` (do **not** patch the reset vector — the unpatched ROM boots to
  the on-screen text menu, which is what we want to see).

### Changed: `src/Aemula.Console/SystemRegistry.cs`

Add `{ "nes", () => new NesSystem() }`. Update the stale comment that says NES
has "no equivalent" of `Television.CurrentRow`.

### Test assets

`nestest.nes` currently lives under
`src/Aemula.Tests/Emulation/Chips/Ricoh2A03/Assets/`. Add a copy (or a shared
`Content` glob) reachable by `aemula-console` runs and by a new
`NesSystemTelevisionTests`.

## PPU render scope — background only, enough for `nestest.nes`

`nestest` in interactive mode programs the palette + nametable 0 and enables
background rendering; it needs no sprites, no mid-frame scroll, no mapper beyond
NROM. Implement the standard NESDev background path:

- **Loopy registers**: `v`, `t`, `x`, `w` — writes to $2000/$2005/$2006, reads
  of $2002 resetting `w`. Replace the current ad-hoc `_ppuScrollPositionX/Y` +
  `_ppuAddress` + `_firstWrite`.
- **Per-dot fetch cycle** (visible + pre-render lines): nametable byte →
  attribute byte → pattern low → pattern high, on the documented dot schedule;
  load the 16-bit pattern shifters + attribute latches at dot 8/16/…; coarse-X
  increment every 8 dots; Y increment at dot 256; copy horizontal bits at dot
  257; copy vertical bits at dots 280–304 of the pre-render line.
- **Pixel mux**: fine-X pick from the shifters → 2 pattern bits + 2 attribute
  bits → palette index → palette RAM → 6-bit colour. `MaskRegister.RenderBackground`
  gates it; the `$3F00` backdrop shows when the pipeline output is 0 or rendering
  is off. Honour `MaskRegister.Grayscale` (mask colour to `$x0`) and left-column
  blanking; **emphasis is ignored** (see below).
- **CHR reads** go through the existing multiplexed-pin path in
  `NesSystem.DoPpuCycle` / `ReadChrRom` (NROM, no banking).

Sprites, sprite-0 hit timing, and scrolling are out of scope but the fetch loop
should be shaped so they drop in later.

## Testing

1. **`Ricoh2C02Tests` revival** — un-skip, extend to run enough cycles with
   rendering enabled to cover active video, burst, and vblank; assert the
   behavioural `vid_sync_*` / `vid_burst_*` / `vid_luma*_*` outputs match
   `Flawless2C02` node-for-node each half-cycle. This is the bit-exact anchor.
2. **Palette decode test** (`NesSystemTelevisionTests`) — for representative
   `LLHH` codes, hold a full-screen background of that colour, run enough frames
   for `Television` to lock, sample the decoded active-video RGB, and compare to
   `Ricoh2C02Chip._systemPalette`'s entry within tolerance. This is the
   NES analogue of the Atari 2600 palette calibration and pins down burst phase
   / hue direction. Tolerance TBD; expect a few % like the other systems.
3. **Frame-lock smoke test** — `FrameRunner.Run(nes, 3)` returns without hitting
   the safety cap (signal locks to a frame boundary).
4. **`nestest.nes` screenshot** — `aemula-console nes --rom nestest.nes
   --frames 10 --screenshot-every 2 --screenshot nestest.png`; eyeball the text
   menu. Per project convention the app is not launched interactively for
   verification — the console screenshot is the check.
5. Targeted `--treenode-filter` runs for `Ricoh2C02Tests` and
   `NesSystemTelevisionTests` only (never the full suite).

## Implementation steps

1. **Loopy `v`/`t`/`x`/`w`** in `Ricoh2C02Chip` replacing the ad-hoc scroll
   fields; keep existing $2002/$2005/$2006 semantics green.
2. **Background fetch loop + shifters + pixel mux** → `Ricoh2C02Chip.Render.cs`;
   expose the per-dot 6-bit colour + an "active video / blank / which sync
   phase" enum.
3. **Video state machine** → `Ricoh2C02Chip.Video.cs`: DAC-tap selection, the
   12-phase chroma counter, sync/breezeway/burst/porch + vblank pulses,
   odd-frame dot skip. Port the prototype's HPos table. Delete the prototype.
4. **Complete the DAC level table** from NESDev / lidnariq; one arbitrary-unit
   array, `_l`/`_h` per tap.
5. **`NesSystem.CompositeVideo.cs`** — byte-scale map, 3:1 box decimator,
   `Television.Decode`, `IHasTelevision`; call it from `NesSystem.Tick()`.
6. **`Ricoh2C02Tests`** revival against `Flawless2C02` — iterate steps 3–4 until
   green.
7. **`SystemRegistry` + `nestest.nes` asset**; run the console screenshot;
   iterate the render pipeline until the menu is legible.
8. **`NesSystemTelevisionTests`** — palette decode + frame-lock.
9. Delete this doc.

## Out of scope / deferred

- **Colour emphasis** (`vid_emph`, $2001 bits 5–7) — `nestest` uses none; leave
  the tap unmodelled with a comment pointing at where the ~120°-wide pull-down
  would go.
- Sprites, sprite-0 hit, sprite overflow timing.
- Mid-frame scrolling / split-screen; mappers other than NROM.
- PAL 2C02 variants (different divider, no odd-frame skip, 3.3-line chroma).
- Controller input (nestest sits on its menu without it — fine for a screenshot).
- A decimating FIR to replace box-of-3 (only if trace comparison demands it).
- Any `Aemula.UI` window work — covered by `docs/emulation-window-plan.md`.

## Risks / open questions

- **Burst phase alignment.** The 2C02's burst tap vs. `NtscYiqDecoder`'s fixed
  −57° burst→I rotation must land hues on `_systemPalette`. Expect one
  calibration landmark (which 12-phase position burst is emitted at), the same
  shape of fix as Apple II's Sather-example and Atari's "burst = hue 1 tap".
  Test 2 is what closes this.
- **`nestest` may not render much.** If the interactive path stalls before
  enabling rendering (unimplemented opcode / missing $2002 timing), the
  screenshot is blank. Mitigations: `--screenshot-every` to watch it build; fall
  back to a tiny hand-written nametable-fill test ROM to exercise the pipeline
  independently.
- **Dot-accurate fetch timing.** `Flawless2C02` comparison is unforgiving about
  exactly which dot each latch loads on. Budget iteration here; the node-by-node
  test makes the mismatch obvious rather than subtle.
- **Odd-frame dot skip vs. Television relock.** Dropping one dot per two frames
  slightly perturbs line length; `NtscRasterOscillators` should absorb it (it
  already tracks Apple II's 912-vs-909.3), but watch the resize deadband churn
  the way Space Invaders' unserrated vsync did.
</content>
</invoke>
