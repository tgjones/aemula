# Space Invaders → composite video — Implementation Plan

## Goal

`SpaceInvadersSystem` currently blits `_ram` straight into a `DisplayBuffer`
once per (software-approximated) frame, with no real horizontal/vertical
timing, no video shift register, and no composite signal at all. This plan
brings it in line with `AppleIISystem`/`Atari2600System`: a pin-level
horizontal/vertical sync chain built from real 74-series counters and gates,
a real 8-bit parallel-in/serial-out shift register serializing VRAM to the
beam, and a composite encoder feeding `Television` — done means a
`TelevisionWindow` opened on `SpaceInvadersSystem.Television` locks onto a
genuine 15.6kHz/60Hz signal and shows the game.

Following this repo's existing conventions:

- `EmulatedSystem` partial-class split, same shape as `AppleIISystem.*.cs`:
  `SpaceInvadersSystem.VideoTiming.cs`, `SpaceInvadersSystem.Video.cs`,
  `SpaceInvadersSystem.CompositeVideo.cs`, alongside the existing
  `SpaceInvadersSystem.cs`.
- New generic 74-series chips as flat files directly under
  `Emulation/Chips/` (no companion folder — same rule as `Ttl74166Chip.cs`
  today).
- Per-chip datasheet-driven tests in `Aemula.Tests/Emulation/Chips/`, flat,
  mirroring `Ttl74166ChipTests.cs`.
- System-level tests in a new
  `Aemula.Tests/Emulation/Systems/SpaceInvaders/` folder (doesn't exist yet),
  shaped like `Atari2600SystemTelevisionTests`.

## Hardware reference (primary source — read this session)

The repo's own `README.md` links a "L shaped board schematics" PDF from
robotron-2084.co.uk; that link now resolves through a document-viewer wrapper
rather than a raw file, which is presumably why it read as a dead end before.
This session followed the wrapper's signed download link through to the
actual PDF (`AA017742A`, Pacific Manufacturing Co., Ltd — this is the
original **Taito** "L-shaped" CPU board, not the Midway American clone board
that MAME's `mw8080bw.cpp` driver comments and computerarcheology.com
describe) and read the CPU/RAM/video sheet directly. **This schematic is now
the primary net-level reference for this plan**, with MAME's driver header
and the MiSTer `Arcade-SpaceInvaders` RTL core kept as secondary
corroboration for timing constants that aren't legible at the resolution
available.

Confirmed directly from the schematic this session (designators are the
Taito board's silkscreen references — they will **not** match MAME/
computerarcheology's Midway-board designators like `C4`/`A5`/`A6`/`D5`/`E5`;
both describe the same function on different board layouts):

| Function | Taito designator | Part | Confidence |
|---|---|---|---|
| Video shift register | `4F` | 74166 (PISO) | High — pin-level traced |
| Composite blanking gate | `6B` | 74LS55 (2-wide 4-input AOI) | High — chip ID confirmed; exact input wiring not fully traced (see Open risks) |
| H/V counter chain | `5H`, `5J`, `5C`, + one more | 74161 ×4 | High — chip ID and cascade confirmed |
| HBLANK/VBLANK/interrupt latches | `3J` (×2 packages), `5C` | 7474 | Medium — packages located, exact half-per-signal assignment not traced |
| CPU/video RAM address mux | multiple, feeding "RAM A0"–"RAM A11" | 74157 (quad 2:1 mux) | High — this is the real hardware for the CPU/video DRAM arbitration in Phase 3 |
| Master clock → CPU/video phase generator | near X-TAL | 74160 (decade counter) + 7442 (BCD decoder) | High — resolves a conflict in secondary sources (MAME's header says "a D flip-flop at B5"; a repair-log source says "a 74LS42 decoder at B5" for the *Midway* board — this schematic shows the *Taito* board uses a 74160+7442 pair for the same role, closer to the repair log's account) |
| `READY` (CPU wait-state) net | breaks out to an RC-filtered test/connector pin | — | High — confirms real hardware CPU/video bus contention exists (see Phase 3) |

Video shift register pin mapping (`4F`, traced pin-by-pin): `SER` grounded,
`CLR` pulled to +5V (never cleared in normal operation), parallel inputs
labelled `D0`→internal `H`, `D1`→`G`, `D2`→`F`, `D3`→`E`, `D4`→`D`,
`D5`→`C`, `D6`→`B`, `D7`→`A`, output `QH` → net labelled `VIDEO OUT`.
`Ttl74166Chip`'s existing shift order (`Ser→A→B→…→H`, `Qh` is the tap) means
loading `D0..D7` into `H..A` and reading `Qh` serializes **`D0` first,
`D7` last — LSB-first**, the same direction `AppleIISystem.DrawHiresByte`
already uses and the same direction the current `UpdateDisplay`'s
`mask <<= 1` loop already assumes. No bit-order flip needed anywhere in this
plan.

Secondary sources (MAME/MiSTer), used for constants not independently
re-derived from the schematic scan this session:

- [MAME `mw8080bw.h`](https://raw.githubusercontent.com/mamedev/mame/master/src/mame/midw8080/mw8080bw.h) / [`mw8080bw.cpp`](https://raw.githubusercontent.com/mamedev/mame/master/src/mame/midw8080/mw8080bw.cpp) header comment — H/V timing constants and interrupt-trigger prose, high confidence, cross-checked against the MiSTer RTL below.
- [MiSTer `Arcade-SpaceInvaders_MiSTer` `rtl/mw8080.vhd`](https://github.com/MiSTer-devel/Arcade-SpaceInvaders_MiSTer/blob/master/rtl/mw8080.vhd) — schematic-derived RTL, used to cross-check the interrupt trigger expression, the video-RAM scan address formula, and the shift-register load/shift condition.
- [Computer Archeology — Space Invaders Hardware](https://www.computerarcheology.com/Arcade/SpaceInvaders/Hardware.html) — the source the *current* code comments cite; superseded by the schematic + MAME/MiSTer above where they disagree (see "Correcting the existing code" below).
- [whitearcade.de repair log](https://www.whitearcade.de/sites/rep_spaceinvaders.php) / [RevSpace repair log](https://revspace.nl/Space_invaders_repair) — Midway-board designator identifications, used only as corroboration.

## Correcting the existing code

`SpaceInvadersSystem.cs:74–106`'s video-timing comment block is wrong and
should be deleted, not preserved: it states **317** pixel clocks per
scanline (`_pixelClock`, `30432`/`71008`/`10161`/`83200`), sourced (per its
own comment) from computerarcheology.com. The real figure, confirmed by both
MAME's driver header and the MiSTer RTL's counter reload constants, is
**320** pixel clocks/line, **262** lines/frame:

- `HTOTAL = 320` (0x140), horizontal rate ≈ 15.600kHz (not the ~15.75kHz the
  current comment implies).
- `VTOTAL = 262` (0x106), frame rate ≈ 59.54Hz.
- H counter free-runs 0→255 visible, reloads to 192 during HBLANK (64-count
  blanking interval).
- V counter free-runs 0x20→0xFF visible (224 lines), reloads to 0xDA during
  VBLANK (38-line blanking interval) — this is also why VRAM only needs
  video data for `V ∈ [0x20, 0xFF]`; `$2000`–`$23FF` (`V < 0x20`) is never
  scanned and is free work RAM.
- Interrupt triggers: `RST 1` (vector `0xCF`) when the V-counter reaches
  `0x80` (displayed line 96, mid-screen) with `VBLANK` low; `RST 2` (vector
  `0xD7`) when it reaches `0xDA` (displayed line 224, VBLANK start) with
  `VBLANK` high. Once Phase 2 below builds the real V-counter, **both
  interrupts and both vector values fall out of `(64V, 128V, VBLANK)`
  directly — no magic pixel-clock constants survive**, exactly the outcome
  the Apple II plan got from its own H/V counter chain.

The `+10161` offset in the current code is very close to `32 lines × 317 =
10144` — almost certainly a (slightly-off, since it used 317 not 320)
attempt to compensate for the V-counter's real `0x20` start value, which
stops being needed once a real V-counter exists.

## Fidelity approach

Per the project's standing guidance: chip-level by default, plain C# only
for genuinely analog circuitry with a documented reason. Two places in this
plan use C# instead of a chip class, both precedented by
`AppleIISystem.CompositeVideo.cs`:

1. **The composite video/sync/blanking summing stage.** Real hardware mixes
   `VIDEO OUT` (from `4F`) and `COMP BLANKING` (from `6B`) into one analog
   composite waveform through a resistor network before it leaves the
   board — this session's schematic read located the `1K` resistor on
   `VIDEO OUT`'s path but not the full summing network or its exact
   resistor values (not yet traced to the connector's `Composite Video
   Output` pin). This will be a weighted sum computed in C#, landmark-
   calibrated the same way `AppleIISystem.CompositeVideo.cs` derives its
   `WVideo`/`WSync`/`BlackVoltage`/`WhiteVoltage` weights, rather than
   guessed values — **confirm the exact network from the schematic during
   Phase 5** before locking in constants; if it can't be traced, calibrate
   against known-good sync/black/white levels the same way Apple II did.
2. **The fractional resampler feeding `Television.Decode`.** Real Space
   Invaders hardware has no color subcarrier at all — it's a monochrome
   composite signal, nothing on the board runs at or is derived from
   3.579545MHz, and real hardware never resamples anything: it just drives
   its native pixel-clock-derived composite signal straight to a
   monochrome monitor. The resampling need is purely an artifact of
   *this codebase's* `Television.Decode`, whose contract assumes every
   caller samples at exactly 4×fsc (14.318180MHz) — a rate Apple II and
   Atari 2600 get for free because their master clocks genuinely equal
   (Atari) or quadruple (Apple II) that number, each because *they* do have
   a real subcarrier tied to their clock. Space Invaders' master clock
   (19.968MHz) has no such relationship to 14.318180MHz to build on — the
   exact ratio is 13125/18304 samples per master tick. There's no real chip
   to model here (there's nothing on real hardware that does this), so it
   has to be a phase accumulator in C#, emitting a `Decode` call each time
   it crosses 1.0. **This is new architecture for this codebase** (the
   television-plan groundwork explicitly assumed a fixed relationship to
   fsc) — flagged as a Phase 5 checkpoint, not something to implement
   silently.

Everything else — the H/V counter chain, blanking/interrupt latches, video
shift register, CPU/video RAM arbitration, clock-phase generation — gets a
real chip class wired net-by-net, the same spirit as `AppleIISystem`.

Space Invaders' composite signal carries **no color burst** (the cabinet's
color is entirely a physical cellophane overlay on the monitor glass, no
video circuitry involved) — the encoder in Phase 5 should output luma+sync
only, with no synthesized chroma component. `Television`'s
`ColorBurstPll.BurstDetected` will correctly read false; that's expected,
not a bug to work around. The cellophane overlay geometry (`invaders.lay`'s
per-region tint boxes, if ever wanted) is a `TelevisionWindow`/UI-layer
feature, not composite video — **out of scope for this plan**.

## Chip inventory (new files under `src/Aemula/Emulation/Chips/`)

| Part | Function | New file | Reused by |
|---|---|---|---|
| 74160 | 4-bit decade (÷10) counter, async clear, sync load | `Ttl74160Chip.cs` | Clock-phase generation (Phase 2) — sibling of the existing `Ttl74161Chip` (÷16); same shape, different modulus |
| 7442 | BCD-to-decimal decoder | `Ttl7442Chip.cs` | Clock-phase generation (Phase 2) |
| 74157 | Quad 2-to-1 mux, common select | `Ttl74157Chip.cs` | CPU/video RAM address arbitration (Phase 3) — note this repo already has `Ttl74153Chip` (dual 4:1) and `Ttl74257Chip` (quad 2:1, tri-state) but not the plain quad-2:1 74157 |
| 7455 | 2-wide 4-input AND-OR-INVERT | `Ttl7455Chip.cs` | Composite blanking gate (Phase 5) — `Y = !((A1&&B1&&C1&&D1) || (A2&&B2&&C2&&D2))`, same computed-property style as `Ttl7420Chip`/`Ttl7402Chip` |

Already available and reused as-is: `Ttl74161Chip` (H/V counters),
`Ttl7474Chip` (blanking/interrupt latches), `Ttl74166Chip` (video shift
register), `Ttl7400Chip`/`Ttl7402Chip`/`Ttl7404Chip`/`Ttl7408Chip`/
`Ttl7420Chip` (glue logic), `MB14241Chip` (already wired).

## Phased plan

**Phase 1 — New TTL chip classes**
Implement and datasheet-test `Ttl74160Chip`, `Ttl7442Chip`, `Ttl74157Chip`,
`Ttl7455Chip` per the inventory above, independent of Space Invaders itself
(same precedent as the Apple II plan's Phase 1). **Done when:** each has a
truth-table/count-sequence test passing in `Aemula.Tests`.

**Phase 2 — H/V sync chain + interrupts**
New `SpaceInvadersSystem.VideoTiming.cs` partial: four `Ttl74161Chip`
instances forming the H-counter (0→255, reload 192 on HBLANK) and V-counter
(0x20→0xFF, reload 0xDA on VBLANK), plus `Ttl7474Chip` instances for the
HBLANK/VBLANK latches and the interrupt-trigger latch, named/exposed the
same way `AppleIISystem.VideoTiming.cs` does (`Hblank`, `Vblank`, etc.).
Delete `_pixelClock`/`30432`/`71008`/`10161`/`83200` entirely; `_cpu.Int`
and `_nextInterrupt` are driven purely from `(64V, 128V, Vblank)` as derived
above. **Done when:** RST 1 fires at displayed line 96 and RST 2 at line
224 with no magic constants left in the file, and existing gameplay
(coin/start/shoot input handling) still works end-to-end.

**Phase 3 — CPU/video RAM arbitration (wait states)**
Wire the `74157` address muxes so the RAM address bus is the CPU's address
during CPU cycles and `V[7:0]:H[7:3]` during video-scanner cycles (matching
the `RAB` formula the MiSTer RTL derives: `V×32 + H[7:3]`, landing the first
scanned address at `$2400` as documented). Drive `Intel8080Chip`'s `Ready`
pin low when the CPU tries to touch RAM out of turn with the scanner,
inserting real wait states. This is the one part of this plan that reaches
back into `Intel8080Chip`/`TickCpuClock`'s existing half-cycle timing
(`docs`'s now-deleted 8080 half-cycle plan) rather than being purely
additive — budget real time for getting the interaction right without
breaking the existing T-state edge protocol. **Done when:** a CPU
instruction that reads/writes RAM during an active video-scan window
measurably stalls for the right number of T-states, verified via the
debugger's instruction trace/logic analyzer.

**Phase 4 — Video shift register + per-pixel display**
Wire `Ttl74166Chip` (`4F`) exactly per the pin mapping traced above: `Ser`
grounded, `Clr` held high, parallel inputs loaded from the VRAM byte at the
scanner's current address, `Clk` from the pixel clock, `Qh` is the video
data bit for the current pixel. Replace `UpdateDisplay`'s frame-end bulk
blit with an incremental per-pixel write into the existing `DisplayBuffer`
during active video (same buffer/`ScreenDisplayWindow` mechanism kept
alongside `Television`, following `AppleIISystem`'s precedent of exposing
both). **Open item to confirm during this phase:** the exact `H` bit(s)
gating `SH/LD` (load vs. shift) weren't legible at the resolution available
this session — MiSTer's RTL uses `H[2:0] == 3`, a reasonable starting
assumption, but confirm against the schematic once implementing. **Done
when:** the game's picture renders correctly in the existing
`ScreenDisplayWindow`, pixel-for-pixel equivalent to what `UpdateDisplay`
produces today.

**Phase 5 — Composite encoder + `Television`** *(confirm approach before
implementing — see Fidelity approach above)*
New `SpaceInvadersSystem.CompositeVideo.cs`: `Ttl7455Chip` (`6B`) generating
`COMP BLANKING` from the H/V sync-window signals Phase 2 produces; a
weighted luma+sync summing stage (no chroma — see Fidelity approach); the
13125/18304 fractional phase accumulator feeding `Television.Decode`;
`public readonly Television Television`, `CurrentCompositeVideoSample`, and
a `CompositeVideoSampled` event for the logic analyzer, matching
`Atari2600System.CompositeVideo.cs`'s shape. **Done when:** `Television`'s
`ColorBurstLocked` is (correctly) always false, but `SampleBuffer` locks
into a stable ~320×262 raster and `IsActiveVideo` frames the picture
correctly.

**Phase 6 — Debugger wiring**
`SpaceInvadersDebugger` adds `new TelevisionWindow(_system.Television)`
alongside the existing screen window, plus a `"Composite Video"`
`ChannelGroup` in `CreateChannelNodes()` (`Channel.Analog`, sync/white
levels), matching `Atari2600System.CreateChannelNodes()`'s pattern.

**Phase 7 — Tests**
New `Aemula.Tests/Emulation/Systems/SpaceInvaders/` folder:
`SpaceInvadersSystemTelevisionTests` (run enough frames for the raster
oscillators to lock, assert `Television.SampleBuffer` shape and
`RasterRegion.ActiveVideo` framing — shape borrowed from
`Atari2600SystemTelevisionTests`), plus targeted tests for the new Phase 3
wait-state behavior and Phase 2's interrupt-trigger correctness.

## Comments convention for this feature

Per this repo's established style for video-timing code (see
`AppleIISystem.VideoTiming.cs`, and the now-deleted `television-plan.md`'s
explicit convention section): every non-obvious hardware claim in the new
code gets a comment naming its source and confidence, the same way this
plan does above. Where a value in this plan is marked "confirm during
implementation," the eventual code comment should record what was actually
found on the schematic, not just repeat this plan's placeholder.

## Open risks / questions

- **`COMP BLANKING`'s exact gate inputs, and whether VSYNC is serrated**,
  weren't legible at the scan resolution available this session. An
  unserrated 4-line VSYNC would cause `Television`'s horizontal raster
  oscillator to lose lock for ~3–4 lines once per field (it recovers via
  the same reacquisition path added for a real Atari 2600 capture — see
  `NtscRasterOscillators.cs`) — cosmetic, but worth confirming against the
  schematic before or during Phase 5 rather than being surprised by it.
- **The composite summing network's exact resistor values** weren't traced
  to the connector pin this session (see Fidelity approach above) —
  Phase 5 needs either those values or a deliberate landmark-calibrated
  stand-in, decided at that point, not now.
- **The `SH/LD` load-tap bit** for the `4F` shift register (which `H`
  counter bit(s) trigger a parallel load vs. shift) wasn't traced this
  session — Phase 4 assumes `H[2:0] == 3` from the MiSTer RTL as a starting
  point, to be confirmed against the schematic.
- This plan's schematic is the **original Taito board**; the ROMs already
  in `Roms/` (`invaders.h/g/f/e`) are MAME's standard **Midway** romset
  name. Functionally identical per cross-checking against MAME/MiSTer (both
  of which target the Midway board and agree with this schematic's
  320×262 timing, interrupt logic, and shift-register behavior) — but if
  something doesn't match during implementation, that's the first place to
  look.
- One chip near the crystal oscillator (schematic label read as `75365`,
  low OCR/legibility confidence) wasn't identified — flagged for
  confirmation during Phase 2, not blocking the rest of the plan since its
  neighbors (`74160`+`7442`) already account for the clock-phase generation
  this plan actually needs.
