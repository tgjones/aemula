# Atari2600System → real NTSC output — Implementation Plan

## Goal

Get `Atari2600System` to the same place `AppleIISystem` is already at: producing a
real analog NTSC composite-video signal, decoded live by
`Aemula.Emulation.Output.Television`, viewable in the existing (system-agnostic)
`TelevisionWindow`. Concretely, done means: load a cartridge that paints color
bars, run the system, and see correct-looking color bars in `TelevisionWindow` —
plus a unit test in a new `Atari2600SystemTests` area that asserts the same
thing against `Television.SampleBuffer`, the way
`AppleIISystemTelevisionTests` already does for Apple II.

Along the way, per the request that kicked this off, two chips get a pin-level
foundation they're currently missing:

- `TiaChip` currently exposes a `TiaPins` struct plus `Cycle()`/`CpuCycle()`
  methods the system calls imperatively. It should instead look like
  `Mos6502Chip` — no `Pins` struct, every pin (clock and otherwise) is a
  property directly on `TiaChip`, and setting a clock-input property is what
  *causes* the chip to do work (edge-triggered), the same way
  `Mos6502Chip.Phi0`'s setter runs a CPU cycle when written.
- `Cartridge`/`Cartridge2K`/`Cartridge4K` currently use an internal
  `CartridgePins` struct plus a `Cycle()` method. Real cartridge ROMs have no
  clock pin at all — they're pure combinational address decoders (an EPROM's
  output is just "valid `tPROP` after the address is stable"). So this becomes
  an `Address` property setter that recomputes an `Data` property, no `Cycle()`
  needed.
- `Mos6532Chip` (RIOT) — originally left out of this plan by mistake, now
  included: it has the same `Pins`-struct-plus-`Cycle()`/`CpuCycle()` shape as
  `TiaChip` did, and gets the same treatment (see Phase 1b below).

## Hardware references (verified this session)

`Mos6532Chip` — [MOS 6532 RIOT datasheet](https://6502.org/documents/datasheets/mos/mos_6532_riot.pdf)
(28-pin, single-phase clock, unlike the 6502/6507's internal two-phase
generator): one clock pin (`PH2`), plus `CS1`/`CS2`/`RS` for external address
decode, the same "no glue-logic decoder chip needed, just wire address lines
straight into CS pins" design `TiaChip`'s CS0-3 already reflects. On the 2600,
RIOT's `CS1`/`CS2` are believed to be wired from A7/A12 the same way TIA's
`CS3`/`CS0` are (mirroring the existing, already-correct
`address_7_12`-based dispatch in `Atari2600System.DoCpuCycle`) — exact pin
polarity not independently re-confirmed against a 2600-specific schematic
this session (lower confidence than the TIA wiring above, which chester.me's
build log confirmed directly); functionally, this doesn't change *which*
address range selects RIOT, only how that selection gets expressed (CS pin
writes vs. an external `switch`).

Real, unmodified Atari 2600 hardware only ever produces an RF signal — there's
no real "composite Atari 2600" to trace wiring from for Phase 4's summing
stage. Everything else below (TIA pinout, clock wiring, chip-select wiring,
cartridge port) is real, sourced hardware behavior:

- **TIA 40-pin pinout** — confirmed via
  [Atari Gaming Headquarters' TIA reference](https://atarihq.com/danb/tia.shtml):
  Pin 4 = Φ0 (output, to 6507), Pin 26 = Φ2 (input, from 6507), Pin 11 = OSC
  (input, crystal), Pin 3 = /RDY (output), pins 21/22/23/24 = CS3/CS2/CS1/CS0,
  Pin 25 = R/~W, Pin 2 = SYNC (output), pins 5/7/8 = LUM0-2 (output), pin 9 =
  COL (output), pin 6 = BLK (output), pin 10 = DEL (input), pins 35-40 = I4,
  I5, I0-I3 (inputs), pins 27-32 = A0-A5, pins 14-19 + 33-34 = D0-D7 (only D6/D7
  bidirectional). This lines up exactly with the existing `TiaPins.cs` field
  list — nothing there was wrong, it just needs to become properties instead
  of struct fields.
- **TIA↔6507 clock wiring** — confirmed via
  [chester.me's Atari 2600 breadboard build, part 3](https://chester.me/archives/2021/06/atari-2600-on-a-breadboard-part-3-tidying-up-and-adding-the-TIA-video-chipe/):
  the crystal drives TIA's OSC pin; TIA divides by 3 internally and drives its
  Φ0 *output* into the 6507's Φ0 *input*; the 6507's own internal Φ2 output
  feeds back into TIA's Φ2 *input* pin. **TIA generates the CPU clock**, not
  the other way around — this is the opposite direction from how
  `Atari2600System.Tick()` currently drives things (it currently calls
  `DoCpuCycle()` first, then `_tia.Cycle()` three times).
- **TIA chip-select wiring** — same source: TIA CS0 ← A12, CS3 ← A7, CS1 tied
  to +5V, CS2 tied to GND. Net effect (exact active-high/low polarity of each
  pin TBD against a real schematic during implementation, but the *selected
  address range* is not in question): TIA is selected exactly when A7 and A12
  are both low — which is exactly the `address_7_12 == 0b0000000000000000`
  case `Atari2600System.DoCpuCycle` already special-cases today. RIOT's
  existing `RS`/`A`/`RW` wiring in the same method (A9 → RS) also already
  matches documented behavior and isn't changing.
- **Cartridge port pinout** — confirmed via
  [pinouts.ru's Atari cartridge pinout](https://old.pinouts.ru/Motherboard/AtariCartridge_pinout.shtml)
  and [Nocash's 2600 specs](https://problemkaputt.de/2k6specs.htm): 24-pin
  edge connector, A0-A12 + D0-D7 + GND + +5V, and "A12 pin is used as CS
  (chip select, active HIGH)" — exactly matching the existing
  `GetBitAsBoolean(pins.A, 12)` gate in `Cartridge2K`/`Cartridge4K`.
- **Composite tap point for the "modded" output (Phase 4)** — confirmed via
  [RetroSix's "Extracting Composite Video (Atari 2600)"](https://www.retrosix.wiki/extracting-composite-video-atari-2600)
  and the [Tynemouth Software composite mod writeup](http://blog.tynemouthsoftware.co.uk/2015/02/atari-2600-video-modification.html):
  real mods tap directly off TIA's own SYNC/LUM0-2/COL pins (pins 2, 5, 7, 8,
  9), before the RF modulator, and sum them through a resistor network into
  one composite signal. This is the same shape as
  `AppleIISystem.CompositeVideo.cs`'s weighted-resistor summing stage, just
  with different inputs — see Phase 4.
- **TIA's COL pin is already a documented approximation in this codebase** —
  `TiaPins.cs`'s existing `Col` doc comment notes real TIA has "a digital
  phase shifter... to provide a single color output with fifteen (15) phase
  angles" on one analog pin, but this codebase outputs a 4-bit hue index
  instead "for now". Confirmed independently via
  [lospec's Atari 2600 TIA NTSC palette notes](https://lospec.com/palette-list/atari-2600-tia-ntsc):
  128 colors = 8 luma levels × (15 real chroma phases, evenly spaced, +
  achromatic hue 0 = grey). This plan keeps that existing approximation as-is
  (not in scope to fix) — Phase 4 just needs to turn the 4-bit `Col` index
  back into an approximate phase (`hue * 24°`) to synthesize a chroma signal
  for the composite output, per the next point.
- **What the raw COL pin signal actually looks like — square, not sine.**
  Asked directly this session, and confirmed via an
  [AtariAge thread specifically on this question](https://forums.atariage.com/topic/293400-shape-of-the-output-signal-on-tia-pin/)
  (fetched via a text-proxy since the forum itself 403s automated fetches):
  the TIA does **not** output a sine wave on pin 9. Internally it's a phase-
  delayed digital square wave — a shift-register/delay-line clocked at the
  color-subcarrier rate, tapped at a point that varies with the hue value (0
  delay/no signal for hue 0 = grayscale) — and that raw square wave only
  becomes sine-shaped *after* going through a bandpass filter, which on real
  hardware is external board circuitry between the TIA and the RF modulator
  (or, for a composite mod, between the TIA and the composite jack — the same
  resistor/cap network the Tynemouth/RetroSix sources describe already
  implies some filtering, just not documented to the component level). This
  corrects an unstated assumption in the original draft of this plan (that
  COL was already analog-sine on the pin, by analogy with Apple II's color
  burst). **Practical effect on Phase 4 below: little to none** — synthesizing
  an approximate sine at the hue's phase (rather than a raw square wave) is
  still the right target for the *composite-jack* signal `Television.Decode`
  expects, since that's downstream of the implied filtering either way; it's
  just now correctly understood as "approximating the filtered output of a
  phase-shifted square wave," not "reproducing what's on the TIA pin
  directly."

## Phase 1 — `TiaChip` pin foundation

Replace `TiaPins` and the `Cycle()`/`CpuCycle()` entry points with properties
directly on `TiaChip`, following `Mos6502Chip`'s shape exactly:

- **`Osc`** (settable `bool`): replaces `Cycle()`. Setting it high runs the
  color-clock body that `Cycle()` currently contains (playfield/player timing,
  `ExecuteClockLogic`, `DoPlayfield`, HMOVE handling, etc.) — unchanged logic,
  just moved into this setter's edge-triggered branch. `TiaChip` internally
  keeps the existing `ClockDiv4`-style divide-by-3 counter and, every third
  rising edge, updates the `Phi0` output below — this is the piece that moves
  *into* `TiaChip` from `Atari2600System.Tick()`'s current `_tiaCycle` field
  (see Phase 3).
- **`Phi0`** (read-only `bool` property): TIA's clock output to the 6507.
- **`Phi2`** (settable `bool`): replaces `CpuCycle()`. Setting it (fed from
  `_cpu.Phi2`, mirroring how `Mos6502Chip.Phi0`'s setter is fed from the
  system today) runs the existing register read/write body, gated on the
  chip-select state below — real TIA only responds to Φ2 while its CS pins
  select it.
- **`CS0`/`CS1`/`CS2`/`CS3`** (settable `bool`): as wired in hardware
  (`CS0`←A12, `CS3`←A7, `CS1`/`CS2` fixed levels — see references above).
  `Atari2600System` drives these directly from address bits instead of
  deciding "is TIA selected" itself and only conditionally calling a method.
- **`Rdy`, `Sync`, `Blk`, `Lum`, `Col`, `Del`, `Address`, `Data05`, `Data67`,
  `RW`, `Aud0`, `Aud1`, `I`** — same fields as today's `TiaPins`, just promoted
  to top-level properties with the same names, preserving existing internal
  reads/writes (`Pins.Foo` → `Foo`) throughout `TiaChip.cs`,
  `PlayerAndMissile.cs` (`tia.Pins.Lum` → `tia.Lum`), and `TiaUtility.cs`.

No behavior change to the actual video/audio logic in this phase — it's a
mechanical reshaping of the entry points, verified by the existing TIA
register-write `switch` bodies staying byte-for-byte identical.

**Call-site updates**: `TiaWindow.cs` doesn't touch `Pins` directly (it
already reads `_tia.VerticalSync` etc.), so it's unaffected. Anywhere else
using `_tia.Pins.X` (only `Atari2600System.cs` today) updates in Phase 3.

## Phase 1b — `Mos6532Chip` pin foundation

Same treatment as Phase 1, applied to RIOT. Drop `Mos6532Pins`; add:

- **`Phi2`** (settable `bool`): replaces both `Cycle()` and `CpuCycle()`.
  Real RIOT has one clock pin (unlike the 6502 family's internal two-phase
  generator) — on the relevant edge it always ticks the interval timer
  (today's `Cycle()` body — "the timer counts on the falling edge of phi2",
  per the existing comment, so this fires on the high→low transition
  specifically, not both edges) and, gated on chip-select, does the
  RAM/register access (today's `CpuCycle()` body, unconditionally invoked
  today — the gating below is new).
- **`CS1`/`CS2`** (settable `bool`): real RIOT's external chip-select pins —
  see the hardware-references note on these below. `Phi2`'s setter only runs
  the RAM/register body when they indicate RIOT is selected, the same
  CS-gating idea as `TiaChip.Phi2` in Phase 1.
- **`Res`, `RW`, `Irq`, `DB`, `A`, `PA`, `PB`, `RS`** — same fields as today's
  `Mos6532Pins`, promoted to top-level properties with the same names. `Res`
  becomes edge-triggered like `Mos6502Chip.Res` (today's `CpuCycle()` checks
  `pins.Res` as a level and immediately clears it back to `false` itself,
  which is a slightly odd read-and-self-clear pattern for what's meant to be
  an external pin — worth fixing to a real edge check while this is already
  being touched, but flagging it as a small behavioral tweak rather than a
  pure reshape, unlike everything else in Phase 1/1b).

**Call-site updates**: nothing outside `Atari2600System.cs` touches
`_riot.Pins` today, so this phase only affects that one file (Phase 3) plus
`Mos6532Chip.cs`/`Timer.cs` internally (`pins.Foo` → `Foo`).

## Phase 2 — `Cartridge` pin foundation

Drop `CartridgePins`. `Cartridge` (and `Cartridge2K`/`Cartridge4K`) gets:

- **`Address`** (settable `ushort`): setter immediately recomputes `Data` from
  the backing ROM array (`GetBitAsBoolean(value, 12)` gates it exactly as
  `Cycle()` does today) — no clock needed, matching a real EPROM's
  combinational behavior.
- **`Data`** (read-only `byte`): the recomputed value.

`Cycle()` is deleted entirely (nothing calls it once `Atari2600System` sets
`Address` directly instead). `ReadByteDebug` stays as-is — it's a debugger
memory-view helper, unrelated to the pin interface.

This is intentionally the smallest possible change: no bankswitching support
is being added here, just reshaping the existing 2K/4K ROM lookup to a pin
interface. A real bankswitching cartridge (not in scope) would still fit this
shape later — hotspot detection is itself just address-snooping, no clock
needed there either.

## Phase 3 — `Atari2600System` master-clock rewiring

`Tick()` currently: runs a CPU cycle every 3rd call via a manual `_tiaCycle`
counter, then always calls `_tia.Cycle()`, then feeds a `TelevisionSignal`
into the old `Aemula.Television`. Per the hardware references above, TIA
should drive this, not the system:

1. `Tick()` toggles `_tia.Osc` (false→true edge), matching one 3.579545MHz
   master-clock pulse. `_tiaCycle` and the manual "every 3rd tick" logic are
   deleted — that division now lives inside `TiaChip.Osc`'s setter (Phase 1).
2. After the `Osc` edge, `Atari2600System` reads `_tia.Phi0` and drives
   `_cpu.Phi0 = _tia.Phi0` (a plain getter→setter propagation, the same
   pattern already used for `_cpu.Rdy = _tia.Rdy`). `DoCpuCycle`'s current
   `_cpu.Phi0 = false; _cpu.Phi0 = true;` pair goes away — the system no
   longer decides when the CPU clocks, TIA does.
3. Address decode: instead of the `switch (address_7_12)` dispatch that
   manually decides RIOT vs. TIA and calls `CpuCycle()`/`Cycle()`
   conditionally, `Atari2600System` unconditionally drives address/data/RW
   onto **both** chips every cycle (as real hardware does — every chip sees
   the whole bus) plus their CS/RS pins from the relevant address bits (A7,
   A9, A12 — same bits as today, just expressed as pin writes), then sets
   `_tia.Phi2 = _cpu.Phi2` and `_riot.Phi2 = _cpu.Phi2`. Both chips now decide
   for themselves (via their own CS pins, set up per Phase 1/1b) whether that
   edge means "do a register/RAM access" — `Atari2600System` no longer
   branches on address bits to decide *which* chip's cycle method to call, it
   just always clocks both and lets each one's own CS-gated `Phi2` setter be
   the thing that's selective, same as real hardware.
4. `_cartridge.Pins.A = ...; _cartridge.Cycle();` becomes
   `_cartridge.Address = (ushort)(_cpu.Address & 0x1FFF);` — reading
   `_cartridge.Data` afterward, no explicit cycle call.
5. The old `ntsc.tv` `BinaryWriter`, `ConvertRange`, and the `Aemula.Television`
   /`TelevisionSignal` plumbing at the bottom of `Tick()` are deleted outright
   — replaced by Phase 4's summing stage feeding the new `Television`.

## Phase 4 — composite video synthesis (checkpoint before implementing)

New `Atari2600System.CompositeVideo.cs` partial class, same overall shape as
`AppleIISystem.CompositeVideo.cs`: every `Osc` tick, read TIA's `Sync`/`Blk`/
`Lum`/`Col` outputs and produce one composite-video voltage sample, fed into
`Television.Decode`.

Proposed formula (mirroring the Apple II file's technique of solving weights
from known landmark voltages rather than needing exact real resistor values):

- If `Sync`: output sync-tip level.
- Else if `Blk`: output blanking level.
- Else: output blanking level + a luma step from `Lum` (0-7, linearly spaced
  black→white — real hardware uses a weighted 3-bit ladder off TIA pins
  5/7/8, per the RetroSix/Tynemouth sources above, but the exact per-bit
  weights aren't sourced yet — a linear 0-7 ramp calibrated to the same
  black/white landmarks the Television decoder already expects is the
  simplest faithful stand-in) + a chroma sine at phase `Col * 24°` (amplitude
  0 when `Col == 0`, i.e. grayscale — see the COL-pin note in the references
  section above), using a running master-tick phase counter the same way
  `AppleIISystem.CompositeVideo.cs` derives its burst sine's phase from
  `_masterTickCounter`.

**This is the one place in the plan that isn't "follow the real chip" by
construction** — real 2600s don't output composite at all, so the "weighted
sum, landmark-calibrated" approach above is a design choice modeled on how
real composite mods and the existing Apple II code both do it, not something
pulled from a schematic. Flagging this explicitly per the instruction to
check before deviating from actual-hardware behavior: **confirm this approach
(or an alternative) before Phase 4 is implemented**, same as Phase 1-3 don't
need that check because they're mechanical/pinout-verified.

`Television` samples must arrive at exactly 4× the NTSC color subcarrier
(`Television.Decode`'s documented contract) — same as Apple II. TIA's OSC
*is* the 3.579545MHz color clock already (one `Osc` edge per master tick), so
this is naturally already at the right rate — one `Decode` call per `Osc`
edge, no resampling needed, unlike Apple II which samples at its own 14.318MHz
master clock (4× *its* dot clock, not directly the NTSC subcarrier) and has to
account for that separately.

## Phase 5 — swap in the new `Television` type

- `Atari2600System.Television` becomes a `public readonly` field of
  `Aemula.Emulation.Output.Television` (matching `AppleIISystem.Television`),
  replacing the private `_television` field of the old `Aemula.Television`.
- `Atari2600Debugger.CreateDebuggerWindows` adds
  `result.Add(new TelevisionWindow(_system.Television));`, matching
  `AppleIIDebugger`'s existing registration.
- **Cleanup — narrower than originally scoped.** `DisplayBuffer.cs` (root
  `Aemula` namespace) actually defines three separate types, not one:
  `Television` (the old digital, `TelevisionSignal`-based class),
  `TelevisionSignal` itself, and `DisplayBuffer` (a plain pixel buffer with no
  dependency on either of the other two). `SpaceInvadersSystem` holds its own
  `DisplayBuffer` directly and renders it via `ScreenDisplayWindow` — nothing
  to do with `Television`/`TelevisionSignal` — so `DisplayBuffer` **stays**.
  Only `Television` and `TelevisionSignal` become dead once `Atari2600System`
  is the only other thing touching them (confirmed by search: the remaining
  hits are doc-comment mentions in `TelevisionWindow.cs` and
  `AppleIISystem.CompositeVideo.cs` explaining the naming collision, plus the
  already-`[Skip]`ped `TelevisionTests.cs` prototype — neither is real usage).
  So this step is: delete the `Television`/`TelevisionSignal` declarations
  out of `DisplayBuffer.cs`, leaving the `DisplayBuffer` class itself and the
  file in place, and separately delete `TelevisionPalette.cs` in full (its
  only consumer is `Television.Signal`, which is going away). Still worth
  keeping as its own reviewable step, not folded silently into Phase 5's
  diff.

## Phase 6 — tests

New `src/Aemula.Tests/Emulation/Systems/Atari2600/` directory (doesn't exist
yet), mirroring the Apple II test layout:

- **`Atari2600SystemTelevisionTests.cs`**: builds a small color-bar cartridge
  image as an inline `byte[]` (hand-assembled 6507 machine code — a color-bar
  kernel is only a handful of instructions: per scanline, `STA COLUBK` with a
  different immediate value for each of a few horizontal bands, `STA WSYNC`,
  loop; small enough to write and comment inline the way this codebase
  already writes small fixed byte sequences elsewhere, rather than adding a
  `dasm` build step for one tiny program), loads it via
  `system.LoadProgram(...)` (needs a temp-file path, unlike `AppleIISystem`'s
  `LoadProgram("")` — `Atari2600System.LoadProgram` reads cartridge bytes from
  disk), ticks a handful of frames to let `Television`'s sync/raster
  detection lock (same reasoning as `AppleIISystemTelevisionTests.BootToIdle`
  — except Atari 2600 has no boot ROM, so this is "run N frames", not "boot to
  idle"), then asserts distinct regions of `system.Television.SampleBuffer`
  decode to the expected distinct hues — same `ActiveVideo`-only scanning
  technique `AssertUniformLitHue` already uses, generalized to check 2-3
  bands land on 2-3 different expected colors rather than one uniform hue.
- Also worth a basic **`Atari2600SystemTests.cs`** (doesn't exist yet either)
  for non-video sanity — e.g. that `LoadProgram` + a few ticks doesn't throw,
  CPU actually runs — same minimal-smoke-test role
  `AppleIISystemTests.cs` plays.

Per project convention, these are **not** run as part of the full suite by
default expectation — use `--treenode-filter` scoped to the new test class
while iterating, the same as any other touched test class here.

## Suggested implementation order

Phases 1, 1b, and 2 are independent of each other and of Phases 3-6 (pure
reshaping, verifiable by existing behavior staying identical — e.g. TIA and
RIOT register read/writes still work the same, cartridge ROM reads still
return the same bytes). Phase 3 depends on all three. Phase 4 needs the
Phase 4 checkpoint resolved before it's implemented. Phase 5 is small once
3-4 exist. Phase 6 can start as soon as Phase 5's `Television` field exists,
even against a placeholder/incomplete Phase 4 formula, and is really what
proves Phase 4's formula is right.
