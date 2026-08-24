# Atari2600 → correct absolute color phase — resolved

**Status: done.** Implemented and verified 2026-08-24. This doc is kept as
the record of what was wrong and why, because an earlier draft of it reached
the *opposite* conclusion — that absolute color phase was inherently a
per-source calibration needing a software "tint knob" — and that conclusion
was wrong in an instructive way. See "What the earlier draft got wrong"
below before reaching for a tint control again.

## Goal (as originally stated)

Get Atari2600System's decoded colors to actually look like Pitfall's real
palette through `TelevisionWindow` — not just internally consistent
(distinct hues, correctly ordered, stably locked) but recognizably the right
hues, rather than a real, consistent picture that is globally rotated
(magenta background, lavender ground, cyan brick).

## Root causes

Three separate 180° errors, cancelling in pairs, plus one wrong constant.

### 1. `NtscYiqDecoder.BurstToIAxisRotationRadians` was 180° out

It carried a `PllLockBranchDegrees = 180.0` term on top of the correct
spec-derived −57°, justified by the claim that `NtscColorBurstPll`'s phase
detector is "squaring/Costas-style" and so cannot distinguish a lock from
one 180° away.

That claim is false. The PLL correlates the incoming sample *directly*
against its cos/sin references and uses the quadrature accumulation alone as
its error term — it never squares the signal and never multiplies its
in-phase and quadrature arms together, which is the step that creates a
Costas loop's sign blindness. For burst `A·sin(90n + b)` against a reference
at `90n + P`, the error is `cos(b − P)` and the update is
`P -= gain·cos(b − P)`. Fixed points at `P = b ± 90°`; only `P = b − 90°` is
stable. **One stable lock, the same for every signal.** There was never a
source-dependent branch for a per-instance constant to resolve.

### 2. `smpte.ntsc` transmits its burst 180° from where RS-170A puts it

This is what the +180 was really compensating for. Measured directly out of
the asset's raw bytes — correlating each bar's chroma against the same
line's burst window, i.e. a phase *difference*, so no axis convention or
signal polarity enters into it:

| bar | phase relative to burst, in the asset | standard vectorscope target |
|---|---|---|
| yellow | 167.5° | 167.1° |
| cyan | 283.9° | 283.5° |
| green | 241.3° | 240.7° |
| magenta | 60.9° | 60.9° |
| red | 103.9° | 103.5° |
| blue | 347.5° | 347.1° |

Sub-half-degree agreement — but those targets are defined with **burst at
180°**, on the −(B'−Y') axis, not at zero. The bars sit at their correct
absolute angles while the burst sits half a turn away: a bug in whatever
synthesized the file (and it is synthesized — every line is byte-identical),
not a property of NTSC.

Fixed at the asset boundary, in `SmpteAsset.LoadNormalized`, by reflecting
each burst window's samples about that window's own mean. Not in the
decoder: a nonconformant signal gets brought back to spec before it reaches
`Television`, exactly as a real receiver is entitled to assume.

### 3. AppleII's own burst phase was also 180° out

`AppleIISystem.CompositeVideo.cs` phased its burst sine off
`_masterTickCounter % 4` — a counter free-running from power-on with no
hardware-derived alignment to the VIDEO DATA shift-register phase that
carries the picture's chroma. It happened to land 180° out, cancelling the
decoder's 180° and making the AppleII tests pass.

Now `(_masterTickCounter + 2) % 4`, calibrated against Sather's worked
example (p.8-15: `$2A`/`$55` by address parity "produces a short green
line"), the same way this file's `EffectiveLogicHigh`/`TransistorVbe` are
solved from Gayler's measured levels.

### 4. The hue step was 24°, not 26.7°

`360/15 = 24` assumes TIA's delay line adds up to a clean single turn. It
doesn't. `Palette.NtscPalette`'s own entries average **27.4°** per step, and
Stella models the step as `DEF_NTSC_SHIFT = 26.7F`, user-adjustable ±4.5°
(`src/common/PaletteHandler.hxx`, exposed as `-pal.phase_ntsc`).

24° accumulated ~2.7° of error per step, i.e. **~38° of drift by hue 15** —
which is what the earlier draft measured as a "~45° per-hue spread" and
wrote off as irreducible nonlinearity in `Palette.NtscPalette` itself. It
wasn't irreducible; it was this constant.

## What the earlier draft got wrong, and why it matters

The draft proposed threading a per-instance `additionalTintRadians` through
`NtscYiqDecoder` and `Television`, giving Atari2600System its own calibrated
rotation while leaving AppleII's alone — reasoning from the (real) fact that
NTSC sets carried a TINT knob and that real 2600 boards carry a color trim
pot, to the conclusion that absolute color phase is inherently per-source.

The physical intuition was sound; it was attached to the wrong parameter.

- **Absolute phase is not per-source. That is what burst is for.** A period
  television did not need re-tinting when swapping a 2600 for an Apple II,
  and neither should this decoder. Burst is transmitted precisely so that
  absolute hue is a fixed point rather than a calibration.
- **What the color pot actually varies is the hue *spacing*** — the delay
  line's total, i.e. `HueStepDegrees` — which is exactly the parameter
  Stella exposes as adjustable, and which no receiver-side tint control
  could correct anyway.
- The draft's algebraic proof that a uniform additive constant on the
  Atari2600System side is a no-op is **correct**, and still worth keeping in
  mind. It just doesn't imply the fix has to live in the decoder: the real
  errors were a 180° decoder constant (not a free parameter — a wrong one)
  plus a wrong *step size*, which is not uniform-additive and does live on
  the Atari side.

The general lesson: `smpte.ntsc` was treated as ground truth for calibrating
a shared constant. It isn't ground truth; it's one synthetic asset with a
bug in it. Two wrongs cancelled, and the error only became visible when a
third, spec-conformant source (TIA, whose burst and hue 1 are the same delay
line tap) was decoded through the same path.

## Result

| | before | after |
|---|---|---|
| Atari hue 1 decoded | 0.1° (blue) | 180.1° (gold axis) |
| mean error vs `Palette.NtscPalette` | +167° | −9.6° |
| worst-case per-hue error | — | 23.3° |
| per-hue spread | 47° | 27° |

Covered by `Atari2600SystemTelevisionTests.EveryHueCodeDecodesCloseToTheReferencePalette`,
which sweeps all 15 hue codes through the real pipeline and asserts each
lands within 30° of the reference palette. AppleII, SMPTE and the NTSC
decoder tests are unchanged and passing.

## Known residuals (deliberately not chased)

- **~10° mean offset against `Palette.NtscPalette`.** Putting hue 1 at
  exactly burst phase is the faithful model (TIA drives `Col = 1` during the
  burst window — burst *is* hue 1), and it decodes to a yellow-green rather
  than a pure gold. Stella hits the same thing and adds a fixed +20° to its
  palette generator, commenting that "−90° + 33° = −57° would create a
  greenish yellow" while "−90° + 53° = −37° creates gold". Adding that here
  would mean either contradicting TIA's own shared burst/hue-1 tap or
  reintroducing a fitted constant in the decoder. Left alone: it is well
  inside this project's "recognizably correct" bar.
- **Saturation reads low.** Decoded |I,Q| ≈ 28 across the Atari hues, versus
  ~40–50 implied by `Palette.NtscPalette` and 78–112 for the SMPTE bars.
  `ChromaAmplitude` is plausibly undersized, which is why decoded colors
  look pastel next to the reference table. Separate from phase; not
  investigated.
