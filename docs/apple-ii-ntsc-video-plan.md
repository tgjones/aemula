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

Per line: 910 master ticks normally, 912 on the once-per-65-lines long-cycle
line (already established in phase 3). 262 lines/frame (non-interlaced).
That's a ~238,420-sample buffer per frame (≈233KB as bytes) — no
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

**Phase 2 — Blanking-gated VIDEO DATA line**
Capture the per-dot bit `TickVideo()` already computes (currently only
passed to `WritePixel`) as a reusable `VideoDataBit`, forced low whenever
`Hbl || Vbl` (matching Gayler's "A9" gate). No new chip/state — this is
just retaining a value that's already computed.

**Phase 3 — Summing formula + sample buffer**
Implement the weighted-sum-then-clamp formula from "Summing formula" above
as a plain calculation, producing one byte sample per master tick into a new
`CompositeVideo` buffer sized per "Sample rate" above. Add the free-running
subcarrier phase counter and burst sine synthesis, gated by
`ColorBurstGate`. **Done when:** sampled output at known scanline positions
matches the anchor table in "Signal representation" (sync=0, black≈64,
white≈255, burst swinging ≈13–102) at the moments the digital signals say
each level should be visible.

**Phase 4 — Verification**
Since there's no visual decode yet to eyeball, verify numerically:
- Anchor-level tests per "Phase 3".
- Line/frame length tests (910/912 ticks/line, 262 lines/frame) against the
  already-implemented video scanner.
- A test resolving the `HiresColorPhase` open question: compare its
  quadrant value at a given column against `tick & 3` from the new
  free-running subcarrier counter, across several scanlines, to settle
  whether it's actually phase-locked to an absolute reference or only
  relatively consistent.

**Phase 5 — Oscilloscope integration**
Surface both the new digital signals (phases 1–2) and the new analog
composite output (phase 3) in the existing Apple II oscilloscope view.
`docs/oscilloscope-plan.md` already anticipated exactly this as a deferred
stretch phase ("Phase 5 (stretch, later) — Analog channels": "Not started
until that groundwork exists") — this phase is that groundwork. Two parts:

- **Analog channel support in the oscilloscope framework itself.** Add a
  third `ScopeChannel.Kind`, `Analog`, alongside the existing `Digital`/
  `Bus`. This refines `oscilloscope-plan.md`'s original sketch: that doc
  assumed a "float-sampled" channel type would be needed, but since this
  plan's own "Signal representation" decision already lands the composite
  output as a plain 0–255 byte, no new sample storage type is actually
  needed — `ScopeChannel.Read: Func<ulong>` and `ScopeRecorder`'s existing
  `ulong[]` ring buffer cover it unchanged. What `Analog` needs that
  `Digital`/`Bus` don't provide is different *rendering*: a continuous
  `ImPlot.PlotLine` trace instead of `PlotStairs`' discrete steps or the
  hex-banded rectangles `Bus` channels use — stairs/bands would visually
  flatten the whole point of synthesizing the color-burst sine wave in
  phase 3. Y-axis ticks at the meaningful anchor points from "Signal
  representation" above (0="Sync", 64="Black", 255="White"), mirroring how
  `Digital` channels already label their axis "L"/"H" instead of raw 0/1.
- **Wire the new signals into `AppleIISystem.CreateScopeChannelGroup()`.**
  Digital: `HSyncPulse`, `VSyncPulse`, `VideoDataBit` (`ColorBurstGate` is
  already there from phase 3 of the main plan). Analog: `CompositeVideo`,
  the phase-3 byte sample stream. Follows the existing "chips own their
  channel group; systems compose" pattern from `oscilloscope-plan.md` — no
  new framework structure needed beyond the `Analog` kind itself.

**Done when:** the oscilloscope view shows the new sync pulses lining up
correctly against the existing HBL/VBL/`ColorBurstGate` rows, and the
composite video row visibly shows a sine-shaped burst riding on
square-edged black/white/sync steps, landing on the byte anchor values from
"Signal representation" at the moments those levels should occur.

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
  Use this to resolve the "(H5+H4+H8)" open item directly rather than from
  OCR'd prose.
- `Emulation/Output/README.md` — already-collected generic NTSC/CRT decode
  references (crtsim, NTSC-CRT, svofski/CRT, etc.), relevant once decoder
  work starts but not needed for this plan's encoder-only scope.

## Open risks

- The RFI-revision vertical-serration term ("(H5+H4+H8)") needs re-deriving
  from the schematic image, not from prose — see phase 1.
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
