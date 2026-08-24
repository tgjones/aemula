# Television — Implementation Plan

## Goal

Implement `Aemula.Emulation.Output.Television`: a real analog composite-video
decoder that turns a stream of voltage-sample bytes into a displayed image,
including NTSC artifact color (the thing Apple II hi-res graphics depends on).
Pair it with a new `TelevisionWindow` debugger window (`Aemula.UI`, same
overall shape as `ScreenDisplayWindow` — GPU-texture-per-frame — but a fully
independent implementation, not composed from it; see "TelevisionWindow"
below for why) with Saleae/logic-analyzer-style niceties: current dot
position, and translucent overlays showing where HSYNC/VSYNC/blanking/color
burst fall in the raster.

NTSC only for now, but the plan below adds the seams needed to add PAL later
without restructuring: a `TelevisionStandard` enum and an `Ntsc` naming
prefix on every class whose logic is actually NTSC-specific (which, as it
turns out, is nearly everything here — see "Standard detection seam" below).
No PAL logic, no PAL constants, no speculative shared-base-class guessing
between the two — we don't yet know how much of the NTSC pipeline PAL can
reuse, so that question is deferred to when PAL is real work, consistent
with how this repo already treats "build for what's real today, leave a seam
for the rest" elsewhere (`docs/oscilloscope-plan.md`'s "Analog channels are
deferred entirely" note is the same philosophy applied to Analog scope
channels).

## Comments convention for this feature

Unlike most of the codebase (which assumes a reader who already knows the
domain), the implementation files for this feature should be commented
**assuming the reader has never touched NTSC signal theory** — the same
density and style as the block comment already in `TelevisionTests.cs`
(scanline timing broken down in µs, IRE definitions spelled out, etc.).
Where a formula or constant comes from a real reference (Gayler, Sather, the
broadcast NTSC spec), cite it inline the way
`AppleIISystem.CompositeVideo.cs` already does for its summing formula —
don't just drop a number in.

## Existing state

- `src/Aemula/Emulation/Output/Television.cs` — currently an empty `Television`
  stub plus an incomplete, unused `Oscillator`/`OscillatorUpdateResult` sketch
  (deltaTime-based). This plan **replaces** that sketch — see "Raster
  oscillators" under Architecture for why a sample-clocked design fits
  better than the deltaTime-based one already there.
- `src/Aemula/Emulation/Output/README.md` — reference links (RS-170 handbook,
  NTSC Studio Timing, NTSC Demystified series, and prior-art decoders:
  crtsim, Jake Turner's NTSC decoder, svofski/CRT, NTSC-CRT). Worth reading
  before touching the sync-separation and YIQ demod phases below.
- `src/Aemula.Tests/Emulation/Output/TelevisionTests.cs` — a throwaway
  prototype (`[Skip]`ped, uses `System.Drawing.Bitmap` which doesn't run on
  CI). It sketches sync detection and a naive luma-only decode with no
  chroma/YIQ handling. Useful as a sketch of the sync-separation math (its
  IRE/timing comments are accurate) but **not** something this plan builds on
  directly — the real implementation supersedes it. This file gets replaced
  outright by the new tests (see Testing below), not extended.
- `src/Aemula.Tests/Emulation/Output/Assets/smpte.ntsc` — 955,500 raw
  composite-video sample bytes, one byte per sample, value range **0–200**.
  Arithmetic (`955500 / 910 = 1050 = 525 × 2`) confirms 910 samples/line
  (63.5µs at exactly 4×3.579545MHz) and very likely 2 interlaced NTSC frames
  worth of lines (525 × 2 fields, in the classic broadcast sense) — this
  plan does **not** attempt field-accurate interlace reconstruction (see
  Open risks); the decoder just free-runs its vertical oscillator across the
  whole sample stream as one continuous raster.
- **Naming collision, explicitly out of scope:** there is an older, unrelated
  `Aemula.Television` (root namespace, `src/Aemula/DisplayBuffer.cs`) — a
  digital (not analog-decoding) television model taking pre-decoded
  `TelevisionSignal { Sync, Blank, ColorBurst, Color }` structs, used today
  by `Atari2600System`. This plan does not touch it, rename it, or migrate
  Atari2600 to the new class — different namespace, different problem (no
  raw voltage stream exists for Atari2600 in this codebase). Flagging it here
  only so it isn't confused with the new class while grepping.

## Voltage levels (verified against reference sources)

Two different voltage scales matter here, and they are **not the same
scale** — this was worth double-checking carefully rather than assuming the
number "0V–2.0V" already floating around this codebase was standard.

**This is not a broadcast-vs-cable distinction.** Over-the-air NTSC
broadcast just takes the same baseband composite video waveform described
below and modulates it onto an RF carrier for transmission; a composite RCA
jack delivers that identical baseband waveform directly, no RF step
involved. Same signal, same IRE/voltage convention, same spec, either way —
"broadcast" and "composite cable" aren't two different level standards.

**1. The baseband composite video spec (EIA/RS-170A, informational — useful
for comments, not what `Television` calibrates against):**

| Level | IRE | Voltage |
|---|---|---|
| Sync tip | −40 IRE | −0.286V |
| Blanking (reference black) | 0 IRE | 0V |
| Peak white | 100 IRE | +0.714V |
| Color burst | ~40 IRE peak-to-peak, centered on 0 IRE (blanking), 8–11 cycles (nominally 9), during back porch | ±0.143V around 0V |

Total peak-to-peak signal swing (sync tip to peak white) is 140 IRE ≈ 1.0V.
Broadcast NTSC-M also defines a 7.5 IRE "setup"/pedestal that separates true
black from the blanking reference — a distinction Apple II's encoder does
**not** make (see below). Confirmed against
[Wikipedia's IRE (unit) article](https://en.wikipedia.org/wiki/IRE_(unit)),
[wolfcrow's IRE explainer](https://wolfcrow.com/a-quick-look-at-understanding-ire-values/),
and [SMPTE ST 170](https://pub.smpte.org/pub/st170/st0170-2004_stable2010.pdf)
(also already linked from this project's own
`src/Aemula/Emulation/Output/README.md`). The 8–11-cycle/nominally-9-cycle
burst width and the "Apple ties black to blanking, unlike broadcast" point
were both already independently verified during the Apple II composite-video
work (see `AppleIISystem.CompositeVideo.cs`'s citations and the original
`apple-ii-ntsc-video-plan.md` phase-3 commit) — re-confirmed here, not
re-derived from scratch.

**2. Apple II's actual encoder output (what `Television` is actually
calibrated against, since Apple II is the only real producer in this
codebase today):** already implemented in
`AppleIISystem.CompositeVideo.cs`, sourced from Gayler's *The Apple II
Circuit Description*, ch. 4/App. A, Fig. 4-4 ("Apple II composite video
output, measured open circuit with R11 at maximum") — a citation already
present in this repo's own git history for the encoder work:

| Level | Voltage |
|---|---|
| Sync tip | 0.0V |
| Black (= blanking — Apple ties these together, unlike broadcast NTSC) | 0.5V |
| White | 2.0V |
| Color burst | 0.7Vpp, centered ≈0.45V |

This is the source of the "0V"/"2.0V" figures already in this plan and in
`AppleIISystem.CompositeVideo.cs` — they're real, cited, measured values for
*this specific hardware's* simplified 3-level (no separate pedestal) analog
scale. `Television`'s byte↔voltage mapping (byte 0 ≈ 0V sync tip, byte 255 ≈
2.0V white — see "Input signal contract" below) matches this Apple II scale
specifically, since that's the only real signal available to test against.

**And it's a genuinely non-standard scale, not just a differently-labeled
one.** Gayler's numbers above were measured open-circuit (no 75Ω monitor
load attached). The output stage has a 27Ω series resistor (R10, already
documented in `AppleIISystem.CompositeVideo.cs`'s citations) between the
emitter follower and the RCA jack, so loading it into a real 75Ω monitor
input forms a voltage divider: `75 / (75 + 27) ≈ 0.735`. Applied to Gayler's
table, that's white dropping from 2.0V to ≈1.47V and black from 0.5V to
≈0.37V (sync stays at 0V) — a ~26.5% drop, matching Gayler's own stated
"~30% when loaded" closely enough to confirm the arithmetic. **1.47V loaded
white-to-sync swing is still ~47% hotter than the spec's 1.0V (140 IRE).**
The real Apple II's composite output is genuinely over-spec/non-standard by
design (a cost-cut resistor-summing emitter-follower stage instead of a
proper video-encoder IC), not merely measured or modeled oddly — which is
worth internalizing before the self-calibration design below: even the one
real device this plan targets doesn't hit the textbook numbers, so hardcoding
spec voltages would be wrong even for its only real input source.

**Why the distinction matters for the decoder:** `Television` self-
calibrates its sync/black/white levels at runtime (see "Level tracking"
below) rather than hardcoding either table above, precisely *because* the
two real inputs it needs to handle (Apple II live output, and the
`smpte.ntsc` asset once rescaled onto the same 0–255 byte range) don't
actually agree on exact levels even after rescaling — self-calibration is
what makes that not matter, rather than requiring `Television` to know which
scale it's looking at.

## Input signal contract

`Television.Decode(byte sample)` is called once per sample, and **every
caller is assumed to sample at exactly 4× the NTSC color subcarrier**
(14,318,180 Hz — `AppleIISystem.CyclesPerSecond`, and also what `smpte.ntsc`'s
byte count implies). This one assumption is what makes YIQ quadrature
demodulation simple and cheap (every group of 4 consecutive samples is
exactly one subcarrier cycle, 90° apart — the same trick
`AppleIISystem.TickCompositeVideo` already uses to synthesize its burst sine
via `_masterTickCounter % 4`). It is not a generic any-sample-rate decoder,
and doesn't need to be — nothing in this codebase produces composite video at
any other rate. (This 4×fsc assumption is itself one of the NTSC-specific
things called out in "Standard detection seam" below — PAL's subcarrier runs
at a different frequency, 4.43361875MHz, so a PAL decoder would need its own
sample-rate assumption, not a shared one.)

Byte-to-voltage mapping: byte `0` ≈ 0V (sync tip), byte `255` ≈ `WhiteVoltage`
(2.0V), matching `AppleIISystem.CompositeVideo`'s own Apple-II-specific
encoder scale documented above. `smpte.ntsc`'s bytes are 0–200-ranged on a
different scale, so the test that reads it **rescales `b * 255 / 200` before
calling `Decode`**, once, at the point the file is loaded — `Television`
itself only ever sees the canonical 0–255 mapping and doesn't know two
producers exist. This fixed mapping is only used to *seed* the self-
calibrating level tracker's initial estimate (see below) — it's a reasonable
starting guess, not a hard assumption the decoder depends on for
correctness.

## Architecture

New/changed files:

```
src/Aemula/Emulation/Output/
  Television.cs               — public front door: Decode(byte), DisplayBuffer,
                                 Standard, CurrentColumn/CurrentRow/IsActiveVideo
  TelevisionStandard.cs        — enum { Ntsc } (single member today — see
                                 "Standard detection seam" below)
  Ntsc/
    NtscSyncSeparator.cs        — self-calibrating level tracking (sync/black/
                                 white) + HSYNC/VSYNC pulse classification
    NtscRasterOscillators.cs    — horizontal/vertical sample-clocked position
                                 counters, capture-range-limited sync lock
    NtscColorBurstPll.cs        — persistent local-oscillator phase lock to
                                 the color burst (a real PLL — see below)
    NtscYiqDecoder.cs           — comb-filter luma/chroma split, I/Q demod,
                                 YIQ→RGB weighted-sum output

src/Aemula/UI/
  TelevisionWindow.cs          — DebuggerWindow subclass, independent of
                                 ScreenDisplayWindow (see below)

src/Aemula.Tests/Emulation/Output/
  TelevisionTests.cs                — replaces the current prototype file
  Ntsc/
    NtscSyncSeparatorTests.cs
    NtscRasterOscillatorsTests.cs
    NtscColorBurstPllTests.cs
    NtscYiqDecoderTests.cs
```

One file per class, kept small and separate on purpose (not collapsed into
`Television.cs`) — smaller, single-purpose files over fewer large ones.

### Level tracking (self-calibrating AGC/clamp) — `NtscSyncSeparator`

Three running estimates — sync level, black level, white level — tracked as
slow exponential moving averages seeded with the Apple II nominal defaults
from the table above (0 / ~64 / 255 on the shared 0–255 mapping), so
decoding is sane from sample 1 without waiting to converge:

- **Sync level**: EMA of the minimum sample value seen (a real clamp circuit
  clamps to sync tip every line — approximate that with a fast-attack,
  slow-decay running minimum).
- **Black level**: EMA of the sample value immediately following each
  detected HSYNC pulse's trailing edge (back porch, pre-burst) — the
  reference "0 IRE" point a real decoder's clamp pulse samples.
- **White level**: EMA of the peak sample value seen during active video —
  the AGC reference.

`IsBelowSyncLevel(sample)` thresholds at the midpoint between the sync and
black estimates. This is the same idea `TelevisionTests`'s prototype used
(fixed `syncLevel = 4` / `blankLevel` constants) generalized to track
wherever those levels actually land for the current input.

Composite sync separation feeds `IsBelowSyncLevel` into a running low-pass-
filtered pulse-width estimate (an integrator, same principle as the
prototype's `syncSamples` counter): short pulses (~4.7µs, ~67 samples at
4×fsc) are HSYNC; the long serrated pulses during vertical blanking
integrate up past a dynamically-tracked "this is way longer than a normal
HSYNC" threshold and are treated as VSYNC.

### Raster oscillators — `NtscRasterOscillators`

Two sample counters (horizontal position within the current line, line
position within the current frame) modeled the way a real analog TV's
horizontal/vertical oscillators actually work, **not** as a naive "trust
every sync pulse blindly" counter:

- Each oscillator free-runs at its own current period estimate, seeded from
  the NTSC-nominal value (≈910 samples/line, ≈262.5–525 lines/frame family —
  matching how a real set's horizontal-hold circuit centers on ~15,734Hz).
- Incoming HSYNC/VSYNC pulses only pull the oscillator into phase if they
  land within a **capture range** — a tolerance window (a few percent) around
  where the oscillator currently expects the next pulse — exactly like a
  real horizontal-hold circuit's limited pull-in range. A pulse outside that
  window is treated as noise/spurious and ignored rather than yanking the
  raster position around.
- Once several consecutive pulses land inside the capture range, the
  oscillator's period estimate is refined toward the *measured* pulse
  spacing (this is the "measured, not configured" part — the exact samples-
  per-line/lines-per-frame for *this* signal is learned, not passed in, so
  the same class handles Apple II's 912-samples/line stream and
  `smpte.ntsc`'s 910-samples/line stream without configuration).
- If valid sync hasn't been seen for a while, the period estimate relaxes
  back toward the nominal default and the oscillator keeps free-running
  (flywheel) rather than freezing — same as a real set's picture rolling or
  tearing when it loses lock rather than the picture just vanishing.

**This directly answers "will out-of-range input corrupt the picture the
same way": yes, deliberately.** If the input signal's timing is wildly
outside the NTSC-family range (or too noisy to find any pulse inside the
capture range), the oscillators never lock, `CurrentColumn`/`CurrentRow`
free-run at whatever period they last had, and the resulting image is
visibly torn/rolling — matching real hardware rather than a decoder that
mysteriously always displays a perfect picture no matter what it's fed. The
capture-range width is a free parameter with no single "correct" value from
first principles (real sets vary too, and it interacts with the horizontal-
hold analogy loosely, not exactly) — expect to tune it empirically once real
signals are decoding, same as the AGC time constants (see Open risks).

From the two oscillators' current position, `Television` derives:
- `CurrentColumn` / `CurrentRow` — raster position of the sample just decoded.
- `IsActiveVideo` — whether that position falls in the visible window (past
  back porch + color burst, before front porch) vs. blanking/sync.
- Which named region a position falls in (HSYNC / VSYNC / blanking / color
  burst / active video) — needed for `TelevisionWindow`'s overlay nicety
  (Phase 7).

This replaces the existing `Oscillator`/`OscillatorUpdateResult` sketch in
`Television.cs`: that sketch's `Update(float deltaTime)` shape assumes
continuous time, but every consumer here is sample-clocked (one `Decode`
call = one fixed time step), so a plain sample counter with a capture range
is both simpler and a better fit than porting the deltaTime sketch forward.

### Color burst PLL — `NtscColorBurstPll`

You asked for this to be a real phase-locked loop rather than a per-line
"look at the sign of a few samples" shortcut — here's the design:

- **Local oscillator**: because every sample is exactly 90° of subcarrier
  phase (the 4×fsc assumption above), the local oscillator's "frequency" is
  fixed by construction — it's not tracking an unknown frequency, only an
  unknown **phase offset** (which of the 4 samples-per-cycle counts as
  phase 0°). That offset is a small piece of state that persists for the
  lifetime of the `Television` instance, not something recomputed fresh
  every line.
- **Phase detector**: for each sample inside a detected color-burst window,
  compare it against what the local oscillator predicts at its current phase
  (a plain quadrature comparison — *not* a Costas loop, see below): project
  the burst sample onto the
  oscillator's *quadrature* axis (90° off from the in-phase reference axis).
  When the loop is correctly locked, burst energy lands entirely on the
  in-phase axis and the quadrature-axis projection is ~0; any nonzero
  quadrature projection *is* the phase error signal. Because the error term
  is the quadrature projection *alone* — the burst sample is correlated
  directly, never squared, and the in-phase and quadrature arms are never
  multiplied together — this detector has **no 180° lock ambiguity**: of its
  two fixed points only one is stable, so every signal converges to the same
  lock. (This was originally described here as "Costas-style", which is
  wrong, and a compensating 180° constant in `NtscYiqDecoder` was built on
  that misreading — see that class's `BurstToIAxisRotationRadians` remarks.)
- **Loop filter**: a single-pole exponential low-pass on that phase-error
  signal, scaled by a (tunable, empirically-set) loop gain, nudges the
  persistent phase-offset state a little on every burst-window sample —
  integrating over a whole burst (8–11 cycles) each line, and continuing to
  refine slowly line after line, rather than snapping to a fresh estimate
  every line.
- **Flywheel behavior between bursts**: outside the burst window (the rest
  of the active line, and any line where burst isn't found at all — e.g.
  during vertical blanking) the loop simply holds its last phase offset
  unchanged. No error signal, no update — same as a real burst-locked
  oscillator continuing to "ring" at its last-known phase between bursts
  instead of resetting.

This is a real (if simple — proportional-only, not a full type-2 PI loop;
there's no frequency error to correct given the fixed 4×fsc sampling, so a
pure phase-nudging loop is enough) PLL: persistent oscillator state, a
genuine phase discriminator, a loop filter, and flywheel hold — the same
shape real burst-locked chroma-demodulator circuits use, not a decision made
fresh each line from a handful of sign checks.

### YIQ quadrature demodulation & RGB output — `NtscYiqDecoder`

1. **Luma (Y)**: a comb filter — average each active-video sample with the
   sample from exactly one subcarrier cycle earlier (4 samples back). Since
   we're locked to exactly 4×fsc, this is a clean, cheap way to null out the
   chroma component (which inverts every cycle at points 180° apart within
   the comb window) while passing luma (which doesn't oscillate at fsc)
   mostly untouched — the standard NTSC comb-filter trick, not a hack.
2. **Chroma**: `sample − luma` at that same position.
3. **I/Q demodulation**: multiply chroma by the burst-PLL-locked local
   oscillator's two reference phases, 90° apart (each just a 4-entry
   lookup, since the oscillator is always at one of 4 canonical phases);
   average each product over one subcarrier cycle to get the I and Q
   components at that pixel. Genuine quadrature demodulation — the 4×fsc
   sample rate just makes the reference oscillator trivial to generate.
4. **YIQ → RGB**: **real hardware does this with three resistor-ratio-
   weighted analog summing amplifiers** — the same "weighted sum" pattern
   `AppleIISystem.CompositeVideo.cs`'s Q3 encoder stage already uses, just
   run in reverse and in the decoder rather than the encoder — not a lookup
   table (there's no practical way to build an analog LUT out of resistors;
   a fixed linear color-space transform is exactly what a resistor summing
   network is good at). The standard NTSC/FCC-derived matrix (independently
   confirmed via multiple sources — MATLAB's `ntsc2rgb`, and the classic
   FCC-derived coefficients commonly reproduced in video-engineering
   references):

   ```
   R = Y + 0.956·I + 0.621·Q
   G = Y − 0.272·I − 0.647·Q
   B = Y − 1.106·I + 1.703·Q
   ```

   `NtscYiqDecoder` implements this as the equivalent floating-point
   weighted sum (three multiply-adds per pixel), clamped to `[0, 255]` per
   channel, written into `DisplayBuffer.Data` via `RgbaByte`. Different
   sources give very slightly different coefficients (colorimetry
   definitions drifted slightly across NTSC's history) — this set is
   close enough for this project's accuracy bar, and is the most commonly
   cited one.

### Standard detection seam

`TelevisionStandard.cs` — a new enum, `{ Ntsc }` (one member today). Real
multi-standard TVs auto-detect PAL vs. NTSC from the incoming signal itself
(line/frame rate — PAL is ~625 lines/50Hz vs. NTSC's ~525/59.94 — and burst
behavior — PAL's burst famously *alternates phase* line-to-line, which is
literally what "Phase Alternating Line" refers to). `Television.Standard`
exposes this as a property, hardcoded to `TelevisionStandard.Ntsc` for now
with a comment marking where real detection would plug in later — this plan
does not implement that detection, only the property seam for it.

Every class under `Ntsc/` is prefixed accordingly because, on inspection,
none of this pipeline is actually standard-agnostic: the 4×fsc sample-rate
assumption, the burst PLL's cycle-per-sample phase stepping, the YIQ matrix,
and even the raster oscillators' nominal capture-range center are all tied
to NTSC's specific subcarrier frequency and line/frame timing. Rather than
build a shared base class around a guessed-at common shape, the `Ntsc`
prefix just keeps today's (necessarily NTSC-only) classes clearly
distinguishable from whatever `Pal*` classes get added later, without
committing to a shared abstraction neither signal format's real requirements
have been checked against yet.

### Output

`Television.DisplayBuffer` (the existing `Aemula.DisplayBuffer`/`RgbaByte`
types, same ones `ScreenDisplayWindow` already knows how to render) — sized
to the raster oscillators' current detected samples-per-line × lines-per-
frame, and resized (like the legacy `Aemula.Television`'s
`DisplayBuffer.Resize` already does) if detected timing changes. One output
pixel per input sample in the active-video region; non-active samples either
don't get written (if the buffer starts zeroed/black) or get written
dim/tinted — decide during `TelevisionWindow` Phase 7 once the overlay
regions need real pixels underneath them.

## `TelevisionWindow`

`src/Aemula/UI/TelevisionWindow.cs`, `sealed class TelevisionWindow :
DebuggerWindow` — same overall GPU-texture-upload *shape* as
`ScreenDisplayWindow` (constructor takes the thing to render,
`CreateGraphicsResources` allocates a transfer buffer + texture,
`PrepareOverride` maps and uploads it, `DrawOverride` draws it via
`ImGui.Image`, `Dispose` releases GPU resources), but a **fully independent
implementation** — no shared base class or composition with
`ScreenDisplayWindow`, and its own copy of the texture-upload code. Per your
note, `ScreenDisplayWindow` is slated for removal once `TelevisionWindow`
becomes the only way to view the screen, so tying the two together now would
just create a removal headache later for no real benefit today.

Constructor takes the `Television` instance (not just its `DisplayBuffer`,
unlike `ScreenDisplayWindow`) — the overlay features need `CurrentColumn`/
`CurrentRow`/`IsActiveVideo`/region information from the live decoder, not
just pixels.

## Phased plan

**Phase 0 — Scaffolding + asset normalization**
Delete the current `Oscillator`/`OscillatorUpdateResult` sketch from
`Television.cs`. Add `TelevisionStandard.cs`. Add the `smpte.ntsc` byte-range
normalization (`b * 255 / 200`) at the point the new tests load the file. No
decode logic yet.

**Phase 1 — Level tracking + sync separation (`NtscSyncSeparator`)**
Implement self-calibrating sync/black/white level tracking and
`IsBelowSyncLevel`/pulse-width-based HSYNC vs. VSYNC classification.
**Done when:** a synthetic test (hand-built sample array with known sync
pulse positions, no color) correctly reports HSYNC edges at the right sample
offsets, and a separate synthetic test with a long serrated pulse reports
VSYNC.

**Phase 2 — Raster oscillators (`NtscRasterOscillators`)**
Free-running, capture-range-limited, sync-phase-corrected horizontal/
vertical counters; `CurrentColumn`/`CurrentRow`/`IsActiveVideo`/
`DetectedSamplesPerLine`/`DetectedLinesPerFrame`/current region. **Done
when:** decoding `smpte.ntsc` (normalized) reports `DetectedSamplesPerLine ≈
910` and a frame/field line count consistent with the 525-line arithmetic
worked out above; decoding a captured `AppleIISystem.CompositeVideo` buffer
(912 samples/line, 262 lines) reports those numbers instead, same class, no
configuration change; and a synthetic test with a deliberately out-of-range
"sync" pulse train confirms the oscillators fail to lock (free-run,
`DetectedSamplesPerLine` stays near the nominal default rather than chasing
the bogus pulses) rather than corrupting silently or crashing.

**Phase 3 — Color burst PLL (`NtscColorBurstPll`)**
Per-line burst-window detection feeding the phase detector/loop filter/
flywheel design above. **Done when:** a test over `smpte.ntsc` confirms
burst is detected on (nearly) every active line, and a synthetic test
confirms the loop's phase-offset estimate converges and stabilizes over
several lines of consistent synthetic burst input (proving it's actually
integrating across lines, not just reacting to the latest one).

**Phase 4 — YIQ demodulation + RGB output (`NtscYiqDecoder`)**
Comb filter, I/Q demod, YIQ→RGB weighted sum, `DisplayBuffer` writes.
**Done when:** the `smpte.ntsc` property-assertion test (see Testing) passes
— decoded SMPTE bar regions land at roughly the right hue/luma.

**Phase 5 — `TelevisionWindow`, basic render**
New, independent window class rendering `Television.DisplayBuffer` via its
own texture pipeline. Not wired into any system's debugger yet — exercised
manually (or via a throwaway `Program.cs` hook) until Phase 6 gives it a
real data source.

**Phase 6 — Wire into Apple II debugger + code-verified artifact color**
Add a `Television` instance to `AppleIIDebugger`, register
`TelevisionWindow` in `CreateDebuggerWindows`. Each UI frame (in
`TelevisionWindow.PrepareOverride`, before the texture upload), replay
`AppleIISystem.CompositeVideo` from the last-consumed index up to the
current `CompositeVideoWriteIndex` through `Television.Decode` — i.e. the
window pulls and decodes new samples once per UI frame rather than
`AppleIISystem` pushing every master tick live, keeping this out of the hot
emulation loop and avoiding cross-thread state sharing.

Also add a new `AppleIISystemTelevisionTests` (exact name TBD) test that
pokes documented hi-res byte patterns with known expected NTSC artifact
colors into hi-res video memory, runs a frame, decodes the resulting
`CompositeVideo` through `Television`, and asserts the decoded pixel colors
match — this is the code-verified replacement for "eyeball it and hope it
looks right." Exact byte patterns and their documented expected colors come
from Sather (the full text is available via archive.org — already used as a
source for other Apple II detail in this project) at implementation time,
not guessed now. **Done when:** running Apple II in the debugger and opening
the Television window shows a real decoded raster including visible
artifact color on hi-res graphics, *and* the new test passes against
documented expected colors, not just a visual check.

**Phase 7 — `TelevisionWindow` niceties**
- Dot-position overlay: marker/crosshair at `Television.CurrentColumn` /
  `CurrentRow` on the rendered texture.
- Translucent, color-coded overlays over the HSYNC / VSYNC / blanking /
  color-burst regions of the raster when displaying the full (not just
  active-video-cropped) image — using the per-sample region information
  `NtscRasterOscillators` already tracks — plus a toggle to hide all of them
  and show only active video.
- Status readout (detected line/frame length, burst-lock stability) in a
  toolbar, similar in spirit to `LogicAnalyzerWindow`'s zoom readout.

## Testing

Per your direction: **property assertions against `smpte.ntsc`, not a golden
image.** No reference PNG gets checked in. Specific assertions to write once
Phase 4 lands (approximate SMPTE bar order — white/75%-gray-ish, yellow,
cyan, green, magenta, red, blue — sampled at the appropriate column offsets
once `DetectedSamplesPerLine` is known):

- `DetectedSamplesPerLine` and `DetectedLinesPerFrame` land at the expected
  values (Phase 2).
- Oscillators fail to lock (rather than silently misbehaving) on
  deliberately out-of-range synthetic sync timing (Phase 2).
- Burst is detected on active lines, and the PLL's phase estimate converges
  over several lines of synthetic input (Phase 3).
- Each known bar region's decoded RGB is close (generous tolerance — this
  isn't broadcast-accuracy work) to its expected hue, and luma ordering
  between bars is correct (white brightest, blue/black darkest) (Phase 4).
- Apple II hi-res byte patterns with documented expected artifact colors
  decode to those colors (Phase 6, code-verified per above — not a manual
  visual check).

Plus focused synthetic unit tests per phase (hand-built sample arrays with
known sync/burst/color content) for each `Ntsc*` class in isolation, rather
than routing every case through the full `smpte.ntsc` file.

The current `TelevisionTests.cs` (the `[Skip]`ped `System.Drawing.Bitmap`
prototype) gets replaced outright, not extended — its sync-separation
constants and comments are useful reference while writing Phase 1, but its
`Bitmap`/CI-incompatibility problem means none of its actual test code
should survive into the real suite.

## Open risks

- **Interlacing.** `smpte.ntsc` is very likely 2 interlaced fields; this plan
  deliberately does not reconstruct field ordering/alternation — the
  vertical oscillator just free-runs across the whole sample stream as one
  continuous raster. Worth confirming visually once Phase 4 lands that this
  doesn't produce a badly-combed/wrong image for this particular asset; if
  it does, field handling becomes a real phase rather than a footnote.
- **Capture-range width and AGC time constants** (how tolerant the raster
  oscillators are before rejecting a sync pulse; how fast the sync/black/
  white EMAs adapt) are free parameters with no single obviously-correct
  value from first principles — expect to tune them empirically once both
  real signals (Apple II live output, `smpte.ntsc`) are decoding.
- **Burst PLL loop gain** is likewise a tuned constant — too high and it
  chases noise (hue jitter), too low and it's slow to lock after a
  disruption (e.g. right after vertical blanking). The Phase 3 convergence
  test should catch a badly-tuned gain, but the exact value is still an
  empirical choice, not a derived one.
