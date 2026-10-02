# ZX80 PAL variant + PAL television decode

## Execution notes

Work through the phases in order, committing once each is done and working.
Code comments must stand on their own — explain the *why* in the comment,
never cite this document or a phase number (this file is deleted once the
plan lands). If anything contradicts what's written here (a PAL constant that
doesn't survive checking against the spec, a decoder stage that turns out to
depend on NTSC in a way not listed below), stop and ask rather than improvise.
Don't run the full test suite (~40 min); use `--treenode-filter` per touched
test class.

## Goal

1. A PAL ZX80: the stock UK board (no D11 strap), 312 lines / 50 Hz.
2. A `TelevisionStandard.Pal` decode path in `Television`, sharing everything
   with NTSC that is genuinely the same, so that adding it does not create a
   parallel `Pal*` class family.

The ZX80 is monochrome, so goal 1 only needs PAL *timing*. PAL *colour*
(swinging burst, V-axis switch, YUV matrix) is needed for a "genuine" PAL
path but has no producer in the repo yet, so it comes last and is verified
against a synthetic encoder (see Verification).

## What actually differs between NTSC and PAL

Reading the current pipeline stage by stage, the decoder is far more
standard-agnostic than its `Ntsc*` names suggest:

| Stage | NTSC-specific content | PAL change |
|---|---|---|
| `NtscSyncSeparator` | Only the seed constants (HSYNC width in samples, initial levels). Pulse classification is relative (VSYNC = 3x the self-calibrated HSYNC width; equalizing pulses fall under 0.5x) and PAL's ratios are the same (4.7 µs / 2.35 µs / 27.3 µs). | Seeds only. |
| `NtscRasterOscillators` | Only `NominalSamplesPerLine`/`NominalLinesPerField`. The AFC-vs-trigger split, capture ranges, flywheel and row-scale latch are standard-independent. | Seeds only (64 µs, 312.5 lines). |
| `NtscColorBurstPll` | Burst window position/length (in µs/cycles); the PLL maths itself works at any fsc because 4 samples/cycle is baked in. | Different window; **burst swings ±45° line to line**, so the error term must remove the swing. |
| `NtscYiqDecoder` | (a) -57° burst-to-I rotation and the YIQ→RGB matrix; (b) the 2.5 white-reference gain (100/40 IRE). The 1:2:1 comb luma/chroma split and box-filter demod are generic. | UV axes + YUV→RGB matrix; **V sign flips every line**; reference-white gain 0.7/0.3 = 2.333 (no setup, sync is 300 mV of 1 V). |
| `Television` | `NominalFrontPorchFraction`, the `Standard` property, the `Nominal*` seeds for `SampleBuffer`. Region classification, vertical-blanking detection, 4:3 stretch, active-row detection are all generic. | Constants only. |

So: most of the decoder is shared as-is; one descriptor carries the numbers;
the only genuinely new *behaviour* is two small things in the chroma path
(burst swing, V switch).

## Design

### 1. `TelevisionStandard` becomes the single source of per-standard data

Today it is a one-member enum read in exactly one place
(`Television.Standard`), so it can be replaced without ripple. Make it a
sealed class with `Ntsc` and `Pal` static instances (an "enum with data"):

* Physical timing in seconds/Hz, not samples: line period, HSYNC width,
  front porch, burst start (after the HSYNC trailing edge), burst cycles,
  lines per field, colour subcarrier frequency.
* Level model: reference-white gain from sync swing (2.5 / 2.333).
* Chroma model: burst-to-demod-axis angle, the 2x3 RGB matrix rows, and the
  burst swing (0 for NTSC, ±45° for PAL).

`NtscTiming`'s static constants dissolve into this. Constants that are only
there to build test fixtures (`ActiveVideoStartSamples` etc.) move to the
test project's signal generator.

PAL values to confirm against ITU-R BT.470 / EBU Tech 3213 while writing
them (the numbers below are from memory, not yet checked): fsc
4.43361875 MHz, line 64 µs, HSYNC 4.7 µs, burst starts ~0.9 µs after HSYNC
trailing edge (5.6 µs from the leading edge) and runs 10 cycles, front porch
1.65 µs, 312.5 lines per field.

### 2. Time constants resolve through the sample rate

`TelevisionTiming` (new, internal): a `TelevisionStandard` plus an input
sample rate, exposing the resolved values the decoder stages need
(`SamplesPerLine`, `HSyncWidthSamples`, `FrontPorchFraction`,
`BurstStartSamples`, `BurstLengthSamples`, `SamplesPerField`). Each stage
takes one in its constructor, replacing the static `NtscTiming` reads.

`Television(TelevisionStandard standard, double? samplesPerSecond = null)`
defaults the rate to 4 × fsc — the contract every current producer already
meets (Apple II natively; NES, Atari 2600 and Space Invaders resample to
it). `new Television()` therefore stays NTSC at 14.318 MHz and nothing
existing changes.

**Why a rate parameter instead of making the ZX80 resample to 4 × fsc_PAL
(17.734 MHz), as Space Invaders does:** the ZX80 produces samples at 13.0 MHz (one per oscillator edge) with one
pixel per two samples. Resampling to 17.73 MHz is a 1.364× ratio, so every
pixel would come out 2 or 3 samples wide — visible horizontal jitter on a
display that is all pixel edges. The ZX80 already feeds `Television` at its
native 13 MHz today (828 samples/line against a nominal 909) and relies on the
15% capture range to absorb it; at PAL's nominal 1135 samples/line the same
trick falls outside both the capture range and the 20% period clamp, so the
seeds have to be right. Resolving through the real rate fixes that and also
tightens the NTSC ZX80's seed (825.5 vs 909 nominal). Chroma recovery only
makes sense at exactly 4 × fsc; a mono producer at another rate never
carries burst, so the colour killer keeps it grey, as the ZX80 already is.

### 3. Shared class names stop saying Ntsc

A class called `NtscSyncSeparator` that decodes PAL is misleading, and a
parallel `Pal*` set is the duplication to avoid. Mechanical rename, in its
own commit with no behaviour change:

| Old | New |
|---|---|
| `Output/Ntsc/` (namespace `Output.Ntsc`) | `Output/Composite/` (`Output.Composite`) |
| `NtscSyncSeparator` | `SyncSeparator` |
| `NtscRasterOscillators` | `RasterOscillators` |
| `NtscColorBurstPll` | `ColorBurstPll` |
| `NtscYiqDecoder` | `ChromaDecoder` |
| `NtscTiming` | `TelevisionTiming` |

Ripple: five test files under `Tests/.../Output/Ntsc/`, `TelevisionTests`,
the benchmark, `TelevisionWindow`, `NesSystem.CompositeVideo.cs` (reads
`WhiteReferenceGainFromSyncSwing` — becomes `TelevisionStandard.Ntsc`'s
value), and comments in the other producers and in the `TelevisionStandard`
remark that currently says the `Ntsc*` names are deliberate.

### 4. Chroma: one demodulator, parameterised

NTSC demodulates I/Q (burst −57°) and PAL demodulates U/V (U is 180° from
burst; V is 90° on from U), but both are "multiply by two quadrature
references, box-average one cycle, apply a 2-row matrix to luma". So
`ChromaDecoder` keeps its structure and reads from the standard: the axis
angle, the two coefficient rows (the current `RgbCoeffI`/`RgbCoeffQ` become
rows A/B), and a per-line sign applied to the second axis.

`ColorBurstPll` gains the burst swing. NTSC passes swing 0 and the code path
is today's. For PAL the loop must lock to the *mean* burst phase, and each
line's burst sits ±45° off it. The robust identifier is the line-to-line
phase *difference* (±90°, which wraps unambiguously and does not depend on
the absolute lock), not the sign of the absolute error (which has a 90°
ambiguity during acquisition). The PLL exposes `VAxisSign` (constant +1 for
NTSC) alongside `PhaseOffsetRadians`; `Television` passes both to the
decoder. Which sign is which physical line is a convention that the
synthetic-encoder test pins down; if it is backwards, red decodes as cyan.

PAL-S (no delay line) is sufficient: a clean digital signal has no static
phase error for the Hanover-bar averaging to cancel. A line-delay (PAL-D)
averager is a possible later refinement, not part of this plan.

### 6. Luma filter bypass when the colour killer is engaged

`ChromaDecoder` derives luma with a horizontal 1:2:1 filter over samples n,
n-2, n-4 — at 4 samples per subcarrier cycle this is a notch at fsc, the
digital equivalent of a receiver's chroma trap, which keeps the colour
subcarrier out of the luma path. When no burst is detected the colour killer
already zeroes chroma, but luma still goes through the notch, softening
monochrome sources. Bypass it (luma = the raw sample) while the killer is
engaged. The bypass should be switched by the same `colorBurstDetected` flag
that drives the killer, so it follows the line-by-line behaviour a set would
have. Applies to both standards identically since the filter itself is
standard-independent. Changes NTSC ZX80, Apple I and Apple II text-mode
output, so check those screenshots/tests (including any golden values).

### 5. ZX80 side

* `ZX80System` takes its `TelevisionStandard`. `EmulatedSystem` currently
  initialises `Television { get; } = new()`; add a protected constructor
  taking the `Television` so a system can supply its own (the property stays
  non-virtual and non-null, avoiding the virtual-call-in-ctor hazard noted
  there for audio).
* D11 is fitted iff NTSC: `ReadKeyboardMatrix` returns `0x20` (D6 pulled
  low) or `0x60` (D6 high). The strap is the only hardware difference
  modelled; the same ROM branches on it (confirmed by the NTSC work that
  already stabilises at 262.00 lines).
* `ZX80System.CompositeVideo.cs` needs no change beyond the Television it is
  given; the SYNC/VIDEO summing is standard-agnostic.
* The Television is created at the oscillator edge rate (13 MHz, the rate
  `TickCompositeVideo` runs at), not the 4 × fsc default.
* Registration: `zx80` becomes the stock UK PAL board ("Sinclair ZX80"),
  and `zx80-ntsc` ("Sinclair ZX80 (US/NTSC)") is added. Existing references
  to `zx80` (tests, `--input` scripts, benchmarks) need checking: anything
  asserting 262 lines moves to `zx80-ntsc` or constructs the NTSC board
  explicitly.
* The standard is fixed at construction; auto-detection from the signal is a
  later, separate piece of work.

## Phases

**Phase 0 — ZX80 ticks at the oscillator frequency.** The ZX80 ticked at
13 MHz, one tick per X1 edge. Every other system ticks once per master-clock
cycle and pulses its clock pins inside the tick (the NES does
`Clk = false; Clk = true;`), which still processes both edges in order. Do
the same here: move the old `Tick()` body into `TickOscillatorEdge(bool high)`
and have `Tick()` call it for the rising then the falling edge.
`CyclesPerSecond` becomes 6_500_000. `TickCompositeVideo()` still runs after
every edge, so `Television` keeps receiving exactly the same 13 MHz sample
stream as before: the composite signal is not quantised to the oscillator
(it occasionally changes on the falling edge), so nothing is decimated. The
cassette deck (pulled once per edge) is built at the edge rate, and the logic
analyzer gets a `SampleClock` at the edge rate raised after each edge, the
same mechanism Atari 2600 and Space Invaders use for composite video; the
debugger channels opt in to it. Done when the existing ZX80 tests pass, the
NTSC board still converges to 262.00 lines/frame at 828 samples/line (ticks
per frame halve), and a screenshot is byte-identical to before.

**Phase 1 — Rename and de-static (no behaviour change).** Do the table in
§3, plus replace static `NtscTiming` reads with a `TelevisionTiming` instance
fixed at the NTSC/4 × fsc values. All existing tests pass unmodified apart
from names. Run `TelevisionDecodeBenchmark` before and after to confirm the
instance reads cost nothing measurable (it runs once per composite sample).

**Phase 2 — `TelevisionStandard` as data, PAL timing, `Television` ctor.**
§1–2. NTSC values must reproduce today's constants exactly (the unchanged
SMPTE/Apple II/Atari/NES television tests are the proof). Add PAL values.
Tests: a synthetic monochrome PAL signal (sync + equalizing/broad pulses +
flat picture) at 4 × fsc_PAL decodes to ~1135 samples/line and 312.5
lines/field; the same signal at 13 MHz decodes to ~832/line; vertical
blanking detection and `ComputeActiveVideoRowRange` behave on both.

**Phase 3 — ZX80 PAL.** §5. Tests mirroring the existing NTSC ZX80 frame
test: PAL board converges to 312.00 lines/frame; NTSC board still 262.00; D6
reads high/low as appropriate. Then look at it in the UI.

**Phase 3b — Chroma trap bypass when colour is killed.** See §6. Independent
of PAL, but it lands here because it visibly changes both ZX80 variants.

**Phase 4 — PAL chroma.** §4. Needs the test signal generator below. Tests:
PAL colour bars decode to the right hues and luma order (the same checks as
`DecodesSmpteColorBarsInExpectedHueAndLumaOrder`); the burst-swing ident
recovers from an arbitrary starting phase and from a start on either line
parity; NTSC decode is bit-identical to before (swing 0, sign +1).

**Phase 5 — Tidy.** Status readout in `TelevisionWindow` shows the standard;
`Output/README.md` gains PAL references; update `zx80-plan.md` (phase 7
becomes done, remove this document's pointer) and delete this plan.

Phases 1–3 deliver the PAL ZX80. Phase 4 can be deferred without blocking
that.

## Verification

There is no PAL sample asset (the SMPTE one is NTSC), so Phases 2 and 4 need
a signal generator in the test project: lines, sync/equalizing/broad pulses,
burst, and chroma, parameterised by `TelevisionStandard` so NTSC tests can
share it too.

The risk is circularity. An encoder and decoder written by the same person
agree about each other's mistakes, and this codebase has already paid for
that once (two 180° errors cancelling on SMPTE bars while every
spec-conformant source decoded a half-turn off — see the `IAxisFromVAxis…`
remarks). Mitigations: take the encoder's phase conventions from the spec
numerically (burst at 135°/225° about +U, bars from the standard 100/0/75/0
YUV values) rather than from the decoder; pin a handful of hand-computed
samples of one encoded line in a test that does not go through the decoder;
and use a real capture as the arbiter. The candidate is
[PALindrome's corpus](https://github.com/mattgodbolt/PALindrome/tree/main/corpus)
(Sega Master System II, UK PAL). Two caveats found on inspection: the `.sigmf-data`
files are Git LFS objects (~30 MB each; the repo only holds pointers), and they
are *RF/IF captures* (real-sampled at 20-32 MS/s with the vision carrier at
~3.1-3.6 MHz, chroma IF ~7.5-8 MHz), not baseband composite. Using them means a
test-side vision demodulator (envelope/VSB detect off the IF) and resample to
17.734475 MHz before `Decode` — test tooling, not part of `Television`, but
real work, and demodulator artefacts would need separating from decoder bugs.

## Decisions made

* `zx80` is the UK PAL board; `zx80-ntsc` is the US variant.
* Standard fixed at construction; auto-detection deferred.
* Luma notch bypassed when no burst (§6). Still to confirm during
  implementation how real receivers did this, so the behaviour is modelled on
  evidence rather than assumed.
