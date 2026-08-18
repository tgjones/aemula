# Apple II — NTSC Composite Video Encoder Plan

## Goal

Extend `AppleIISystem` to produce a faithful analog NTSC composite video
signal — an integer sample stream, one sample per master-clock (14.31818MHz)
tick — reproducing what the real board's discrete video-summing circuit
outputs, rather than the RGB `DisplayBuffer` bitmap it produces today.

## Scope

**Encoder only.** This plan covers generating the composite waveform on the
Apple II side — the real board's own circuit, built from parts already on
the schematic. It does **not** cover decoding that waveform back into a
displayable image (comb/notch filtering, YIQ demodulation, CRT simulation).
That's generic NTSC-receiver behavior, not Apple II circuitry, and is
separate follow-on work once `Emulation/Output/Television.cs` (currently an
empty stub with `NotImplementedException` throughout) gets built out. See
"Output surface" below for how this plan's deliverable is meant to hand off
to that future work without depending on it.

This plan is a continuation of `docs/apple-ii-plan.md`'s "Future goal: analog
composite video into `Television`" section, and assumes phases 0–5 of that
plan (video timing chain, TEXT/LORES/HIRES generation) are already built —
they are, per the current `git log`.

## How the real hardware does it

Researched directly from primary sources (see "Reference materials" below),
cross-validated against each other. This corrects/extends what
`docs/apple-ii-plan.md`'s existing "Future goal" section only gestured at.

### The summing circuit

There is no dedicated video DAC or color-encoder chip. Three digital signals
are combined by a simple resistor network into one analog node, buffered by
a single transistor:

- **Q3** (2N3904) — an NPN transistor wired as an emitter follower with
  summing inputs at its base.
- **R7** (1.5KΩ) ← **VIDEO DATA** (the picture bitstream, already forced low
  during blanking by an upstream gate — Gayler's schematic labels it "A9" —
  before it reaches R7).
- **R8** (2KΩ) ← **SYNC** (composite H+V sync).
- **R6** (2.7KΩ) ← **COLOR BURST**, but only after passing through a series
  resistor (R5, 1KΩ) and an **L1/C3 LC tank** (C3 a 5–50pF trimmer, resonant
  ≈3.6MHz) that shapes the raw ~3.58MHz gated square wave into something
  closer to a sine wave, and lets C3 trim delay (= hue).
- **R9** (10Ω, collector→+5V), **R11** (200Ω, emitter→ground, "level
  adjust"), **R10** (27Ω, emitter→RCA jack, protects Q3 from output shorts).
- Two other components (Q6, R27) are earlier-revision leftovers that Gayler
  explicitly says "perform no function" on the board revision this plan
  targets (RFI revision, gate A14-1 supersedes them) — ignore them.

Quoted directly (Gayler, *The Apple II Circuit Description*, ch. 4/App. A):

> "The three signals VIDEO DATA, SYNC, and COLOR BURST are combined in Q3 to
> produce the COMPOSITE VIDEO OUTPUT. Transistor Q3 is an emitter follower
> with summing inputs. The values of the three input resistors R6, R7, and
> R8 are selected to give the required relative levels of sync, black,
> white, and burst in the output."

Sather independently corroborates R6=2.7K as the burst summing resistor and
gives a real user-modification anecdote (raising it to 4.7K to tame color
burst on fussy TVs) — a strong cross-check that this value is right.

### Measured output levels

Gayler's Fig. 4-4 ("Apple II composite video output, measured open circuit
with R11 at maximum"):

| Level | Voltage |
|---|---|
| Sync tip | 0.0V |
| Black (= blanking — Apple ties these together, unlike broadcast NTSC) | 0.5V |
| White | 2.0V |
| Color burst | 0.7Vpp, centered ≈0.45V |

Loaded into a real 75Ω monitor input, Gayler states the output drops ~30%
(mostly across R10) — but the open-circuit table above is what this plan
treats as canonical, since it's the one directly-measured, fully-specified
reference point; loading is a monitor/cable property outside the encoder's
job.

### Composite sync — a real gap in the codebase today

`AppleIISystem.VideoTiming.cs` currently has `Hbl`/`Vbl` (blanking) and
`ColorBurstGate`, but **no actual HSync/VSync/composite-SYNC pulse** — nothing
here has needed one yet, since `DisplayBuffer` only stores decoded pixel
colors, not a signal a monitor would sync to. Building the SYNC input to R8
is new digital work, not just new analog work. From Sather/Gayler (equation
numbering matches the RFI/Autostart-Monitor-era board this project already
targets):

- **Horizontal sync**: `HBL • H3 • H2'` — a 4-H-count pulse, immediately
  followed by the already-implemented `ColorBurstGate` window (matches the
  existing code comment "starting right after HSync").
- **Vertical sync**: `V4 • V3 • V2 • V1' • V0' • VC' • (H5 + H4)` — a
  4-scanline-wide pulse with horizontal-rate serrations folded in via the
  `(H5+H4)` term, "so a negative edge occurs right where horizontal sync
  would normally cause a negative edge to occur" (Sather). This is a
  deliberate, documented simplification versus broadcast NTSC: the Apple
  does **not** generate full pre/post equalizing-pulse trains, because its
  vertical scan isn't interlaced and doesn't need them.
- **Composite SYNC** = HSync pulse OR VSync pulse (a plain OR, matching the
  schematic's `SYNC = C13-C + C13-D`).

All of the above are pure combinational functions of the H/V counter bits
that already exist as chip outputs — so, following the precedent already set
by `Hbl`/`Vbl`/`ColorBurstGate` (plain C# boolean expressions over `H0`–`H5`/
`V0`–`V5`, not individually-instantiated gate chips, since they're stateless
functions of state that's already pin-level simulated upstream), these
should be added the same way: new `=>` boolean properties in
`AppleIISystem.VideoTiming.cs`, not new chip classes.

**Resolved**: the RFI-revision fix changes the vertical serration term from
`(H5+H4)` to `(H5+H4+H3)` — not `(H5+H4+H8)` as an earlier OCR pass read it.
"H8" doesn't fit the 6-bit (`H0`–`H5`) horizontal counter and turned out to
be a 3/8 OCR misread; confirmed against a second, independently-worded
passage in the same book (discussing the "Eurapple" variant of the same
equation) where the OCR renders the digit cleanly as "H3". `H3` is already a
real, in-use signal (it's the same bit the Rev.7+ `HSync` equation uses),
which also makes physical sense as the fix: Rev.7 narrowed `HSync` by adding
an `H3`-dependent term, which is exactly what the vertical serration term
needed re-added to stay aligned.

### Color burst gating and duration

Already fully implemented and correct — `ColorBurstGate` in
`AppleIISystem.VideoTiming.cs` is `!H5 && !H4 && H3 && H2`, a 4-H-count
window. Sather's prose claims "14 cycles" of burst; Gayler's claims "about
nine" (matching the real NTSC spec of 8–11 cycles); the two sources
disagree and neither was independently resolved during research. This
doesn't matter for implementation, though: **the actual gate is already
derived from real chip state in code**, not from either author's prose
count, so whatever duration it produces is the authoritative one — no
further work needed here, only a note not to "fix" it to match either
book's cycle count if that number comes up again.

### What's deliberately left as an approximation

- **Burst shaping** (L1/C3 LC tank → sine): L1's actual inductance wasn't
  findable in either source. Per your call, phase 1 synthesizes the burst
  directly as a ~3.579545MHz sine wave sized to the *measured* 0.7Vpp/0.45V
  center window, rather than trying to derive the LC tank's response from an
  unknown component value. This is an approximation of the shape (which
  **is** well-supported — Gayler is explicit the LC network exists to round
  the burst into something sine-like), not of the amplitude (which comes
  straight from the measured table).
- **Everything else stays a sharp square edge.** Sather's own note on TV
  processing says the Apple deliberately outputs raw square waves for
  video/sync — it's the *receiving* TV's limited bandwidth that rounds them
  into sine-like components, not anything on the Apple side. So: only the
  burst gets smoothed at the source; video/sync transitions should be
  emulated as instantaneous steps between sample values.
- **High-frequency rejection filter** at the RFI-revision video jack: Sather
  lists this as one of the RFI board's changes, but neither source
  confirms a discrete LC filter part at the jack — my best-supported read is
  that it's R10 (27Ω) forming a simple RC rolloff against cable/monitor
  capacitance, not a dedicated filter component, but this is inference, not
  a confirmed fact. Not worth modeling explicitly; the byte-sample
  resolution this plan uses is already a much harder band-limit than a
  passive RC rolloff would be.

## Fidelity approach

Two different fidelity bars for two different layers, matching how the rest
of this project already draws the "chip vs. not" line:

1. **Digital layer (new HSync/VSync/composite-SYNC/blanking-gated-video
   logic)** — full gate-level fidelity, same bar as everything else in
   `docs/apple-ii-plan.md`. It's cheap here because it's all stateless
   combinational logic over bits the phase-3 chips already compute, so it's
   expressed as plain boolean properties, not new chip objects.
2. **Analog layer (Q3 + the resistor network)** — **not** simulated at the
   transistor-physics level. Nothing else in this codebase models analog
   component behavior (the 741 op-amp is explicitly deferred/out of scope in
   the main plan for exactly this reason). Instead, reproduce the circuit's
   *behavior* with a weighted-sum formula, calibrated directly against
   Gayler's measured table — see "Summing formula" below. This was validated
   against Gayler's own worked numeric example (his white-level calculation
   reproduces to within rounding using this exact structure), so it isn't an
   arbitrary curve-fit — it's the same math the book uses, just solved
   directly for the calibration constants instead of assuming exact
   component/logic-level tolerances.

### Summing formula

Using the real resistor values as *relative* weights (only ratios matter,
so kΩ vs. Ω is irrelevant):

```
g_video = 1/1.5   (R7)
g_sync  = 1/2.0   (R8)
g_burst = 1/2.7   (R6, always part of the divider network, even when idle)

w_video = g_video / (g_video + g_sync + g_burst)  ≈ 0.434
w_sync  = g_sync  / (g_video + g_sync + g_burst)  ≈ 0.325
w_burst = g_burst / (g_video + g_sync + g_burst)  ≈ 0.241   (sums to 1.0)
```

Solving for the two unknowns (effective logic-high level and Q3's Vbe drop)
directly from the two known non-burst output levels (black = 0.5V when
video=0/sync=1; white = 2.0V when video=1/sync=1) gives an internally
consistent, physically sensible result: effective logic high ≈3.46V (close
to a real TTL ~3.5V high, a nice independent sanity check), Vbe ≈0.62V
(normal for a 2N3904 at these currents). Sync level (video=0, sync=0) then
falls out to a negative pre-clamp value, correctly clamping to the measured
0V (Q3 cuts off) with no separate special case needed.

Implementation formula, per master-clock-tick sample:

```
v_base = V_HIGH * (w_video * videoBit + w_sync * syncBit)   // syncBit=0 during the actual sync pulse
v_out  = max(0, v_base - V_BE)
if (ColorBurstGate) v_out += burstSine(t)                    // added on top, only during the burst window
```

where `burstSine` is a ~3.579545MHz sine sized to ±0.35V around the black
baseline (reproducing the measured 0.7Vpp / 0.45V-centered window), phase
derived from a **free-running** master-tick counter (`tick & 3` for the
quadrant, or a continuous `sin(2π·tick/4 + φ)` for a smoother wave) — never
reset per-scanline or per-frame, matching real hardware where the subcarrier
is just a fixed division of the one free-running crystal.

This free-running phase counter is also a nice side-benefit: it gives an
authoritative answer to the open question already flagged on
`HiresColorPhase` (whether its column-parity-relative phase formula actually
matches the true absolute subcarrier phase consistently line-to-line) — once
this exists, that can be checked directly instead of needing a bespoke test.
**Resolved in phase 4: yes, exactly** — see phase 4's write-up below.

## Signal representation

Per your call: **byte (0–255), linear in the open-circuit voltage, 0V→0V
line mapped to 0–255 over the 0V–2.0V measured range** (`byte =
round(clamp(v, 0, 2.0) / 2.0 * 255)`):

| Signal | Voltage | Byte |
|---|---|---|
| Sync tip | 0.0V | 0 |
| Black / blanking | 0.5V | 64 |
| White | 2.0V | 255 |
| Burst | 0.1–0.8V | 13–102 |

This anchors to Gayler's actual measured numbers (traceable back to a real
source) while landing in a conventional 8-bit range compatible with the
software-NTSC-decoder ecosystem the `Emulation/Output/README.md` already
references (Blargg-style filters, `NTSC-CRT`, etc.) for whenever decode work
starts.

*(Phase 3 update: the burst row's actual implemented range is `{19, 64,
108}`, not a literal 13–102 span — see phase 3's write-up below for why.)*

## Sample rate

**One sample per master-clock (14.31818MHz) tick** — not per dot (7M), not
per CPU cycle (φ0). This is necessary (not just consistent with the existing
"tick at the master rate" decision in `docs/apple-ii-plan.md`) because the
burst sine needs sub-dot phase resolution: the subcarrier is master/4, so
resolving its waveform at all requires at least 4 samples/cycle, which only
master-tick granularity provides.

Note this requires **de-collapsing** part of the existing video generation:
`TickVideo()` currently computes and draws a whole 7-dot character cell in
one shot at the φ0 boundary (a documented, deliberate simplification from
phases 4–5, since it doesn't affect the *pixel* output). The composite
encoder doesn't need to re-architect that — HBL/VBL/SyncPulse/ColorBurstGate
are already stable, pure functions of the H/V counter bits for the whole
14-tick cell, and the 7 already-known dot values from `TickVideo()`'s
existing loop can be expanded into per-tick samples (2 master ticks per dot)
after the fact. Only the burst sine's phase genuinely needs true per-tick
evaluation, and that's a free-running counter, independent of the collapsed
video generation.

Per line: 912 master ticks — 64 normal 14-tick PHASE0 cycles plus one
16-tick "long cycle", on **every** line, not (as this section originally,
incorrectly, assumed) 910 normally with 912 only once every 65 lines; see
phase 4's "Line/frame length" finding below for the correction and the
direct Sather citation that settles it. 262 lines/frame (non-interlaced).
That's an exact 238,944-sample buffer per frame (≈233KB as bytes) — no
architectural concern, just worth sizing a buffer for rather than
allocating per-scanline.

## Output surface

New public field on `AppleIISystem`, parallel to the existing `Display` and
`HiresColorPhase` — e.g. `CompositeVideo` (byte samples) plus whatever
per-sample framing metadata a future decoder will want (at minimum, the
sample's scanline/frame position; `SyncBit`/`ColorBurstGate` are cheap to
recompute from `H`/`V` state if needed rather than storing them separately).

**Deliberately not** wired into either existing `Television`-named class:

- `Aemula.Television` (root namespace, `src/Aemula/DisplayBuffer.cs`) — the
  class `Atari2600System` already feeds via `TelevisionSignal`. It works by
  taking an already-decoded `Color` byte through a fixed palette and using
  `Sync`/`Blank` purely for beam-position bookkeeping — it does no real NTSC
  composite decoding. That's fine for the TIA (which really does have a
  discrete per-pixel color register), but is exactly the shortcut this plan
  exists to avoid for Apple II HIRES, whose color doesn't exist until
  something actually NTSC-decodes the composite waveform.
- `Aemula.Emulation.Output.Television`/`Oscillator`
  (`Emulation/Output/Television.cs`) — an in-progress, unfinished rewrite
  (`NotImplementedException` throughout) seemingly aimed at a more
  physically-real, continuous-time oscillator/resync model.

Both would need real decoder-side work to consume a genuine composite
sample stream, which is out of scope here per the encoder-only scope above.
Revisit which (if either) this plugs into once decoder work actually starts.

## Phased implementation plan

**Phase 1 — Composite SYNC (digital, new) (done)**
Landed as `HSyncPulse`, `VSyncPulse`, `CompositeSyncPulse`, `SyncBit`
boolean properties in `AppleIISystem.VideoTiming.cs`, following the existing
`Hbl`/`Vbl`/`ColorBurstGate` pattern exactly (plain expressions over
`H0`–`H5`/`V0`–`V5`). The vertical serration term landed as `(H5+H4+H3)`,
resolved from Sather's text directly (see "Resolved" above) rather than
needing the schematic image after all — the OCR ambiguity turned out to be
settleable from a second passage in the same book. Verified by two new
tests in `AppleIISystemVideoTimingTests.cs`:
`HSyncPulseIsFourHCountsImmediatelyBeforeColorBurstGate` (width=4, and
`ColorBurstGate` starts exactly where `HSyncPulse` ends) and
`HSyncAndVSyncPulsesMatchDocumentedEquations` (both signals cross-checked
against the documented boolean equations, independently re-derived from the
packed scanner state, across a full run through the vertical sync region).

**Phase 2 — Blanking-gated VIDEO DATA line (done)**
Landed as a private 7-element `_videoDataBits` array in
`AppleIISystem.Video.cs`, holding the current cell's per-dot bit (phase 3
still owns mapping tick → dot-within-cell, per "Sample rate" above), forced
all-false whenever `Hbl || Vbl` (Gayler's "A9" gate).

This phase's premise — "capture the bit `TickVideo()` already computes" —
turned out to only hold for TEXT and HIRES, which already had a real
per-dot bit from their shift registers. **LORES didn't**: `DrawLoresByte`
only ever produced an already-decoded `RgbaByte` straight from
`LoresPalette`, a deliberate shortcut the existing code comments already
flagged as bypassing composite decoding entirely (real "RGB card" hardware
behavior, not what a standard composite output does). Confirmed from
Sather ch. 8 ("Video Generation", p.8-8) that real LORES hardware has a
genuine bit-serial VIDEO DATA line too: the active nibble loads into a
4-bit "end around" shift register that circulates continuously ("the 4-bit
patterns are circulated... This creates colored patterns which seem like
solid color blocks"). Implemented that directly: `_videoDataBits[dot] =
(nibble >> (x & 3)) & 1`, indexed by absolute pixel `x` (not
dot-within-cell) so the phase carries continuously across byte boundaries,
matching "end around." `Display`'s LORES rendering is untouched (still the
direct `LoresPalette` lookup) — this is purely a new, parallel bit-serial
computation feeding `_videoDataBits`.

**New open item**: which of the 4 nibble bits lines up with which `x % 4`
phase isn't recoverable from the available schematic scan — an
arbitrary-but-consistent choice, not a verified one. Doesn't affect this
plan's encoder-only scope (any consistent, genuinely-periodic bit stream is
electrically equivalent for the composite waveform itself), but matters for
a future decoder trying to reproduce the *exact* real hue — same category
of open item as `HiresColorPhase`'s.

Verified by three new tests in `AppleIISystemVideoModesTests.cs`:
`VideoDataBitIsForcedLowDuringBlanking`, `VideoDataBitMatchesHiresShiftedPattern`
(cross-checked against the same byte pattern `HiresBitZeroIsLeftmostDot`
uses), and `VideoDataBitMatchesLoresCirculatingNibblePattern` (checks the
period-4 pattern lands correctly relative to absolute pixel `x`).

**Phase 3 — Summing formula + sample buffer (done)**
Landed as a new `AppleIISystem.CompositeVideo.cs` file: the weighted-sum
constants (`WVideo`/`WSync`, `EffectiveLogicHigh`, `TransistorVbe`) are
`const double`s derived directly from the resistor values and Gayler's two
measured non-burst levels — the exact arithmetic from "Summing formula"
above, not hand-rounded decimals, so tweaking a resistor value (e.g. the
R6→4.7K burst-taming variant Sather mentions) would automatically
recalibrate everything downstream. `TickCompositeVideo(phase0RisingEdge)`
is called once per master tick from `TickVideoTiming()`, tracking
`_ticksSincePhase0Edge` to map ticks → dot index (`/2`, clamped to 6 — the
long-cycle's 2 extra ticks simply hold the last dot, the approximation
"Sample rate" above already called out) and a free-running
`_masterTickCounter` for the burst sine's phase. Output is a fixed
`CompositeVideo` ring buffer (262×912 capacity, one frame's worst case)
plus a `CompositeVideoWriteIndex`.

Verified by 4 new tests in `AppleIISystemCompositeVideoTests.cs`, all
passing on the first run (a good sign the hand-derived constants are
exactly right, not just close): `SyncTipSamplesAsZero` (byte 0),
`BlackLevelSamplesAsSixtyFour` (byte 64, exactly — the two anchor
equations were solved to make this exact, not approximate),
`WhiteLevelSamplesAsTwoFiftyFive` (byte 255, same), and
`ColorBurstSwingsThroughExpectedLevels`, which found the burst cycles
through exactly `{19, 64, 108}` — close to but not identical to the plan's
original "13–102" estimate, because this formula centers the burst on
`BlackVoltage` (0.5V) rather than Gayler's separately-measured 0.45V
center; the ~6-byte offset is the "small offset differences acceptable"
case "Summing formula" already flagged, now with the actual number on
record instead of an estimate.

**Phase 4 — Verification (done)**
Since there's no visual decode yet to eyeball, verified numerically, in two
new tests in `AppleIISystemCompositeVideoTests.cs` (plus the anchor-level
tests already landed in phase 3):

- **`LineAndFrameLengthMatchDocumentedTickCounts`** measures line length
  directly off `HSyncPulse` rising edges (one per line) and frame length off
  the vertical scanner state's repeat period. It found a real error in this
  plan doc's own "Sample rate" section: **every line is 912 master ticks,
  not "910 normally, 912 on the once-per-65-lines long-cycle line"** as
  originally written there. `Phase0IsElongatedOnceEverySixtyFiveCycles`
  (phase 1's test file) already correctly established "1 long PHASE0 cycle
  per 65" — the plan doc's mistake was reading that as "1 line in 65",
  when a line *is* 65 PHASE0 cycles, so it's really "1 long cycle in every
  line" (64×14 + 16 = 912). `AppleIISystem.VideoTiming.cs`'s own comment
  already had this right ("once-per-scanline 'long cycle' stretch") - only
  this plan doc's restatement of it was wrong, and
  `CompositeVideoCapacity`'s `262 * 912` sizing was already implicitly
  correct (just uncommented as to why). Directly confirmed against Jim
  Sather, *Understanding the Apple II*, ch. 3 ("Timing Generation and the
  Video Scanner"), not just re-derived from this codebase's own prior
  comments: "The duration of the horizontal sequence is equal to 64 normal
  6502 cycles and one long cycle... There are exactly 17030 (65 x 262) 6502
  cycles in every television scan... As a side effect all 1 MHz and 2 MHz
  signals are elongated **once every horizontal line**." 262 lines/frame
  was independently confirmed the same way (the vertical scanner state
  repeats after exactly 262 `HSyncPulse` edges, matching Sather's "262
  state counter... the 262 state sequence represents a vertical scan").
  Fixed the doc text above and two comments in `AppleIISystem.CompositeVideo.cs`
  that repeated the same "once-per-65-lines" error.
- **`HiresColorPhaseMatchesAbsoluteSubcarrierPhaseAcrossScanlines`**
  resolves the open question flagged on `HiresColorPhase`: sampling the
  free-running `_masterTickCounter`'s value (`& 3`, i.e. its subcarrier
  quadrant) at a fixed screen column across a full frame-plus-wraparound
  (270 samples) found it's **exactly constant** every time - the absolute
  subcarrier phase at a given column genuinely is phase-locked to a fixed,
  line-invariant reference, not just relatively consistent within one line.
  This resolves cleanly (not just approximately) precisely *because* every
  line is 912 ticks, a multiple of 4 - had the original "910 normally"
  assumption been right, 910 % 4 == 2 would have made the phase flip by
  half a subcarrier cycle every other line instead. This also matches
  Sather's own stated purpose for the long cycle - "the same beginning
  phase relationship occurs every horizontal line" - now verified directly
  against this codebase's own free-running tick counter rather than assumed
  from `HiresColorPhase`'s formula construction alone.

**Phase 5 — Oscilloscope integration (done)**
Surfaced both the new digital signals (phases 1–2) and the new analog
composite output (phase 3) in the existing Apple II oscilloscope view.
`docs/oscilloscope-plan.md` already anticipated exactly this as a deferred
stretch phase ("Phase 5 (stretch, later) — Analog channels": "Not started
until that groundwork exists") — this phase was that groundwork. Two parts:

- **Analog channel support in the oscilloscope framework itself.** Landed
  as a third `ScopeChannel.Kind`, `Analog` (`ScopeChannelKind.cs`), plus
  `ScopeChannel.Analog(name, Func<byte> read, min, max, ticks)` next to the
  existing `Digital`/`Bus` factories. This refines `oscilloscope-plan.md`'s
  original sketch: that doc assumed a "float-sampled" channel type would be
  needed, but since this plan's own "Signal representation" decision
  already lands the composite output as a plain 0–255 byte, no new sample
  storage type was actually needed — `ScopeChannel.Read: Func<ulong>` and
  `ScopeRecorder`'s existing `ulong[]` ring buffer cover it unchanged.
  Rendering (`DrawAnalogTrace` in `OscilloscopeWindow.cs`) went through two
  approaches: `ImPlot.PlotLine`'s straight-line interpolation between
  samples was tried first, specifically to make the color-burst sine (only
  4 samples/cycle at this sample rate - see below) read as a smooth curve
  rather than a jagged staircase; a follow-up review of the running app
  found this came at a real cost, though, since the encoder's black/white/
  sync portions genuinely are a discrete step at one sample per master
  tick, no between-sample interpolation modelled (see "Sample rate"
  below) - so `PlotLine` was misrepresenting their true square edges as
  sloped ramps whenever zoomed in past one sample per pixel. Landed on
  `ImPlot.PlotStairs`, same as `Digital`, since it's the faithful rendering
  for the majority of the signal (the square-edged portions), even though
  it leaves the burst reading as a jagged staircase rather than a smooth
  sine - a real property of the underlying 4-samples/cycle signal, not a
  rendering artifact to hide. Either way this is still a distinct
  `DrawAnalogTrace` from `Digital`'s `DrawDigitalTrace`, not the exact same
  function, since their hover tooltips differ (raw byte value vs. H/L) and
  a future signal with real between-sample structure might still want
  `PlotLine` back.

  The Y-axis range (`AnalogMin`/`AnalogMax`) and labeled anchor ticks
  (`AnalogTicks`, a `(double Value, string Label)` list) are **properties
  on `ScopeChannel` itself, not constants in `OscilloscopeWindow`** — an
  explicit correction made after an initial pass hardcoded composite
  video's specific 0–255 range and Sync/Black/White labels directly into
  the window. That doesn't generalize: the window is meant to stay
  signal-agnostic, and a future analog channel (e.g. a different Apple II
  signal, or another system entirely) will have its own range and anchor
  points with no reason to match this one's. `Digital`/`Bus` channels leave
  these unset (`0`/`0`/empty list) since they don't use them.
  `OscilloscopeWindow` only contributes a generic, signal-independent
  choice: `AnalogAxisPaddingFraction` (5% headroom around whatever
  `AnalogMin`/`AnalogMax` a channel supplies, so its trace doesn't clip the
  plot edge) and the tick-label mirroring of how `Digital` channels already
  render "L"/"H" instead of raw 0/1.

  That tick-label mirroring itself hit a real bug once an Analog channel
  actually existed to expose it: giving each row's Y-axis native
  `ImPlot.SetupAxisTicks` labels (as Digital's "L"/"H" already did) meant
  ImPlot reserved a label-gutter width that scales with the label text -
  "White"/"Black"/"Sync" is much wider than "L"/"H", so the composite video
  row's plot area, and therefore its x-axis, landed visibly out of sync
  with every other row and the timescale ruler. Fixed in
  `OscilloscopeWindow.cs` by dropping native Y-tick-label rendering
  entirely (`NoTickLabels` unconditionally, on every row, not just Bus's as
  before) in favor of `DrawValueAxisLabels`: a fixed-width gutter sized
  once per frame and reserved identically ahead of every row's own
  `BeginPlot` - including the ruler's - regardless of channel kind or label
  content, with the label text positioned via `ImPlot.PlotToPixels` (while
  the row's plot is still active) but actually drawn after `EndPlot()`, so
  it isn't clipped by ImPlot's own plot-area clip rect. See
  `docs/oscilloscope-plan.md`'s phase 5 write-up for the fuller mechanical
  detail.
- **Wired the new signals into `AppleIISystem.CreateScopeChannelGroup()`.**
  Digital: `HSyncPulse`, `VSyncPulse`, and a new `VideoDataBit` property
  (`ColorBurstGate` was already there from phase 3 of the main plan).
  Analog: `ScopeChannel.Analog("Composite Video", () =>
  CurrentCompositeVideoSample, 0, 255, [(0, "Sync"), (64, "Black"), (255,
  "White")])` — `CurrentCompositeVideoSample` is a new property returning
  the byte most recently written into the phase-3 `CompositeVideo` ring
  buffer, i.e. the current tick's sample, since the oscilloscope samples
  channels once per tick via `Read: Func<ulong>` rather than reading the
  ring buffer directly, and the 0/64/255 anchors are exactly the
  Sync/Black/White byte values from "Signal representation" above.
  `VideoDataBit` reads `_videoDataBits[Math.Min(_ticksSincePhase0Edge / 2,
  6)]` — the exact tick-to-dot mapping `TickCompositeVideo` already used
  inline to compute `videoBit`; that computation was refactored to call the
  new property instead of duplicating the mapping. Both new properties live
  in `AppleIISystem.CompositeVideo.cs`, next to the state they read; all
  new channels were added to the existing "Video Timing"
  `ScopeChannelGroup` rather than a new group, so the sync
  pulses/blanking/composite-video rows sit next to each other for the
  direct visual comparison "Done when" below calls for. Follows the
  existing "chips own their channel group; systems compose" pattern from
  `oscilloscope-plan.md` — no new framework structure was needed beyond the
  `Analog` kind itself.

All 24 existing `AppleIISystem*` tests (including the phase 3/4 composite-video
and video-timing tests) still pass unchanged after the `TickCompositeVideo`
refactor — the new `VideoDataBit` property is a pure extraction of existing
logic, not a behavior change.

**Done when:** the oscilloscope view shows the new sync pulses lining up
correctly against the existing HBL/VBL/`ColorBurstGate` rows, and the
composite video row visibly shows a sine-shaped burst riding on
square-edged black/white/sync steps, landing on the byte anchor values from
"Signal representation" at the moments those levels should occur. The
implementation above is complete and builds/tests clean, but this criterion
is inherently a visual one (an oscilloscope view) — actually eyeballing it
in the running app is left to manual verification, not automated here.

## Reference materials

New/corrected since `docs/apple-ii-plan.md` was written:

- Jim Sather, *Understanding the Apple II* — already in the main plan's
  reference list; this plan additionally leans on chapters 3–4 for the sync
  equations and the burst-gate/duration prose.
- Winston Gayler, *The Apple II Circuit Description* —
  `http://www.apple-iigs.info/doc/fichiers/TheappleIIcircuitdescription1.pdf`.
  **Correction to the main plan doc's framing**: this was previously
  recorded as a 19-page front-matter-only preview
  (memory `appleii-sather-source`) — it's actually 224 pages of real
  content, including a dedicated Chapter 4 ("Video Sync"/"Composite Video")
  and Appendix A ("Video Techniques") that this plan's circuit description
  is drawn from directly.
- Working schematic source (new):
  `https://mirrors.apple2.org.za/ftp.apple.asimov.net/documentation/hardware/schematics/Schematic%20Diagram%20of%20the%20Apple%20II+.pdf`
  — the RFI-revision addendum (Apple part #031-0004-C), 12 legible pages,
  includes the full video-output stage schematic (Q3/R6/R7/R8/R9/R10/R11).
  Not needed in the end for the vertical-serration term (resolved from
  Sather's text directly, see phase 1) — still the best source if the
  LORES nibble-bit-order open item (phase 2) ever needs settling.
- `Emulation/Output/README.md` — already-collected generic NTSC/CRT decode
  references (crtsim, NTSC-CRT, svofski/CRT, etc.), relevant once decoder
  work starts but not needed for this plan's encoder-only scope.

## Open risks

- ~~The RFI-revision vertical-serration term ("(H5+H4+H8)") needs
  re-deriving from the schematic image, not from prose~~ — resolved in
  phase 1: it's `(H5+H4+H3)`, confirmed from a second passage in Sather's
  own text.
- LORES's nibble-bit-to-subcarrier-phase mapping (which of the 4 nibble
  bits lines up with which `x % 4` phase) is an arbitrary-but-consistent
  choice, not verified against the schematic — see phase 2. Doesn't affect
  this plan's encoder-only scope; matters for a future decoder.
- Burst duration prose disagreement (9 vs. 14 cycles) is moot — the existing
  `ColorBurstGate` implementation is already the authoritative source, not
  either book's cycle count.
- L1's inductance is unknown; burst shape is a sine-wave approximation
  targeting the measured amplitude, not a derived LC response. Revisit only
  if a future decoder's color accuracy demands more precision than this
  gives.
- The RFI board's "high frequency rejection filter" at the video jack isn't
  confirmed as a real discrete component (best guess: it's just R10) —
  deliberately not modeled; flagged in case it matters once real decode
  work needs to match a specific frequency response.
