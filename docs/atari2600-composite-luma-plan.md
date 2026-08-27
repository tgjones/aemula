# NTSC composite level calibration + TIA luma/chroma

Started as the Atari 2600 "composite luma model" deferred task; the
investigation showed the right fix is a spec-based recalibration of the whole
NTSC synthesise/decode level scale, with the TIA-specific luma/chroma work
riding on top. Every composite-video producer in the repo is affected.

## Origin

`docs/atari2600-tia-plan.md` carries a "Deferred, separate task" note: the
composite luma model in `Atari2600System.CompositeVideo.cs` maps `lum`
linearly as

```
BlankingLevel + (WhiteLevel - BlankingLevel) * (lum / 7f)
```

so every `lum == 0` colour collapses to blanking level (black) regardless of
hue, even though real 2600 luma-0 rows are black only for hue 0
(`Palette.NtscPalette[$10]` = `0x444400`, a dark olive). That note frames it
as a narrow "luma-0 → black" fix. Investigation (below) shows the luma-0 case
is one visible symptom of a broader miscalibration of the whole
synthesise-then-decode colour path, and the fix necessarily touches the
`Television` decode side as well as `CompositeVideo.cs`.

## What was measured

Rendered `Pitfall (1983) (CCE)` through `Aemula.Console` and compared against
the real title screen. A temporary trace in `TickCompositeVideo` logged, per
scanline for one settled frame, the `(Lum, Col)` pairs TIA actually presents,
plus the decoder's `Luma`/`I`/`Q` and final RGB from `Television.SampleBuffer`.

TIA register output is correct. Pitfall's forest is:

| Element              | TIA `(Lum, Col)` | `Palette.NtscPalette` target      | Our decoded RGB            |
| ---                  | ---              | ---                               | ---                        |
| Sky / gaps (COLUBK)  | `(1, $D)`        | `0x345c1c` dark forest green      | ~`(20, 90, 0)` (row 60)    |
| Canopy foliage (PF)  | `(3, $D)`        | `0x6c9850` mid green `(108,152,80)`| `(54, 242, 0)` blown-out   |
| Solid canopy band    | `(4, $1)`        | `0xb8b840` khaki `(184,184,64)`   | `(189, 255, 7)` blown-out  |
| Tree trunks (M0/M1)  | `(0, $1)`        | `0x444400` dark olive `(68,68,0)` | `(0, 0, 0)` black          |

Reference title-screen samples (from the wiki image) confirm the palette
column is the right target: sky `(43,84,17)`, foliage `(99,147,54)`, trunk
`(61,61,0)`.

### Root cause

Two compounding faults, both in the synthesise/decode calibration, neither in
TIA:

1. **`CompositeVideo.cs` luma curve.** `64 + 191*(lum/7)` has a hard floor at
   `BlankingLevel` (64) for `lum == 0` and runs high through the low/mid
   codes. `Palette.NtscPalette`'s own grey ramp (hue 0) decodes to roughly
   `Y ≈ 0, 64, 108, 144, 176, 200, 220, 236` for codes 0..7 — a compressive
   curve starting at true black, not a line from 64. Separately, chroma-bearing
   hues at `lum == 0` sit near `Y ≈ 60` (their own compressed sub-range),
   *not* at 0 and *not* at black — which is the trunk case.

2. **Decoder gain floats with picture peak.** `NtscSyncSeparator` tracks
   `_whiteLevel` as a fast-attack/slow-decay running *maximum* of non-sync
   samples (an AGC white reference). `NtscYiqDecoder` then scales both luma
   and chroma by `255 / (whiteLevel - blackLevel)`. Pitfall's brightest
   large area is only `lum ≈ 4`, so `_whiteLevel` settles around 150–165
   instead of 255 and the scale balloons to ~2.5×. That stretches every
   colour: `lum 3` synthesised at composite ~146 decodes to `Luma ≈ 209`
   (near white), and the chroma sine (amplitude 24) decodes to an I/Q
   magnitude ~60 against a palette target of ~40. Hence "bright green".

   Apple II content contains real reference white (white text on black), so
   its `_whiteLevel` stays near 255 and this path has always looked right
   there — which is why the fault is 2600-specific and slipped existing
   tests.

`EveryHueCodeDecodesCloseToTheReferencePalette` only checks hue *phase*, on a
solid full-screen colour at `lum 6`, so it never exercised the luma curve,
the saturation magnitude, or a dim multi-colour scene.

## Does this explain Pitfall's bright-green canopy?

**Yes — fault 2 is the direct cause, and it is the same underlying
"synthesise/decode never validated end-to-end against `Palette.NtscPalette`"
problem the deferred note is about.** The canopy foliage is `(Lum 3, Col $D)`
playfield; the decoder's inflated ~2.5× gain drives its luma to near-white and
its chroma well past the palette's saturation, giving a vivid lime green
instead of `0x6c9850`. The trunks (`Lum 0, Col $1`) going black is fault 1,
the literally-deferred symptom. Both are in scope here.

## Does it explain the score-line corruption?

Partly:

- **Different backgrounds on the two score rows.** The register trace shows
  both the `2000` row band and the `20:00` row band are a uniform
  `(Lum 1, Col $D)` — identical TIA output — yet they decode to visibly
  different colours. That is the excess gain (fault 2) amplifying decode
  instability: the 3-tap comb and the 4-sample I/Q box filter leave a
  residue near the high-contrast digit edges, and at ~2.5× gain that residue
  becomes a visible colour shift. **Expect this to mostly resolve once fault
  2 is fixed**; re-capture and check. A residual band mismatch after that is
  an `NtscYiqDecoder` comb/box-filter robustness question — related, but its
  own task.

- **"2000" / "20:00" glyphs not rendering cleanly.** The horizontal
  streaking through the digits is the same comb/gain artefact. Any remaining
  *shape* wrongness in the glyphs is a TIA object-rendering matter (score
  digits are drawn with players/playfield; see the main TIA plan's Phase 8
  playfield cleanup and Phase 10 NUSIZ work) and is **not** part of this
  task. Diagnose separately after the colour fix; do not chase it here.

## Scope

The main TIA plan "does not touch the composite-video / `Television` path".
This task is the counterpart that does — and, per the decision to fix the
decode side properly rather than build more encoding on top of a slightly-off
decoder, it recalibrates the whole NTSC level scale to spec and updates
**every** encoder to match. Files in play:

- `src/Aemula/Emulation/Output/Ntsc/NtscYiqDecoder.cs`,
  `src/Aemula/Emulation/Output/Ntsc/NtscSyncSeparator.cs`,
  `src/Aemula/Emulation/Output/Ntsc/NtscColorBurstPll.cs` — the decoder gain
  reference and the shared byte scale (Phase 1).
- `src/Aemula/Emulation/Systems/Atari2600/Atari2600System.CompositeVideo.cs`
  — landmark levels, luma curve, chroma amplitude, from the TIA output
  resistor network (Phases 2–3).
- `src/Aemula/Emulation/Systems/AppleII/AppleIISystem.CompositeVideo.cs` —
  volts→byte mapping re-anchored on Gayler's measured sync/blanking (Phase 2).
- `src/Aemula/Emulation/Systems/SpaceInvaders/SpaceInvadersSystem.CompositeVideo.cs`
  — `WhiteLevel` constant only; 1-bit signal, no tuning (Phase 2).
- `src/Aemula.Tests/Emulation/Output/SmpteAsset.cs` — normalization target
  (Phase 2).
- `src/Aemula/Emulation/Systems/Atari2600/Palette.cs` — reference only, not
  modified.

Out of scope: TIA object/playfield rendering, `TiaChip.Col` staying a hue
index (unchanged, per its own doc comment), PAL, `NtscYiqDecoder`
comb/box-filter robustness, the score-glyph shape question above, 7.5 IRE
setup (this stays a 0-setup / NTSC-J style scale, as the code already
assumes).

**Comment discipline:** per repo convention this planning doc is deleted once
the work lands — no code comment may cite a phase number or this file. Bake
the *reason* into the comment, matching the density of the existing remarks in
these files (all are already heavily annotated; keep that standard).

## Phase 1 — Spec-anchor the decoder gain, and define one byte scale

The decode gain must not depend on how bright the current scene happens to
be. A real NTSC receiver runs **gated sync AGC** — a detector samples the
signal *only during the sync interval*, measures the sync-tip-to-blanking
excursion, drives IF/video gain to hold it constant — plus per-line **DC
restoration** clamping the back porch to a fixed black reference.
`NtscSyncSeparator` already tracks both those points (`_syncLevel`,
`_blackLevel`, the latter clamped to the sample after HSYNC's trailing edge).
`_whiteLevel` as a running *max* of picture samples is the non-physical
shortcut: it works only when the signal reliably contains reference white
(Apple II text every field) and inflates on a persistently-dim signal like
Pitfall's forest.

### 1a. The shared byte scale

Fix all producers and the decoder to one IRE-derived scale, **0 IRE setup**
(black = blanking; this is what the code already assumes and matches
NTSC-J):

| Point            | IRE (from sync) | Byte  |
| ---              | ---             | ---   |
| Sync tip         | 0               | 0     |
| Blanking / black | 40              | 64    |
| Reference white  | 140             | 224   |
| Sample ceiling   | ~159            | 255   |

i.e. **1 IRE = 1.6 bytes**, `K = (224-64)/(64-0) = 2.5` exactly (= 100 IRE /
40 IRE). Reference white is **224, not 255** on purpose: bright saturated
colours legitimately swing above 100 IRE once chroma rides on luma, and the
31-byte (~19 IRE) headroom to the 255 ceiling lets the decoder's comb still
separate that chroma instead of clipping it flat (this is part of what makes
today's bright colours "washed out"). An implementer may push reference white
lower still if TIA's `lum 7` + full chroma proves to need more than 19 IRE of
headroom (e.g. 1 IRE = 1.4 → white 196, ~42 IRE headroom, blanking 56);
224 is the recommendation, not a hard floor.

`blanking = 64` is kept from today deliberately — it is exactly where
Gayler's measured Apple II blanking (0.5 V) lands under a clean `byte =
volts · 128` map (see Phase 2), so Apple II sync and blanking stay
spec-conformant with no fudge and only its peak white moves.

### 1b. Decoder changes

- `NtscSyncSeparator`: add a public `SyncLevel` getter (field already
  exists). Reseed `InitialBlackLevel`/`InitialWhiteLevel` if it helps
  convergence, but they are only a starting guess.
- `NtscYiqDecoder.Process`: replace `scale = 255f / (whiteLevel - blackLevel)`
  with `whiteRef = blackLevel + K * (blackLevel - syncLevel)`,
  `scale = 255f / (whiteRef - blackLevel)`, `K = 2.5f` as a named const with
  the IRE derivation in the comment. Takes `syncLevel` as a new `Process`
  parameter.
- `Television.Decode`: pass `_syncSeparator.SyncLevel` through to
  `_yiqDecoder.Process`, and pass the same `whiteRef` (not the running
  `_whiteLevel`) to `_colorBurstPll.Process` — the PLL only uses
  `whiteLevel - blackLevel` to scale its burst-detection threshold, so a
  fixed reference there just makes detection more stable. Audit that use
  while changing it.
- `_whiteLevel` tracking stays for the status readout (`Television.WhiteLevel`,
  shown in the UI toolbar) but no longer feeds decode or the PLL.

Because the decoder still self-calibrates `syncLevel` and `blackLevel` from
each incoming signal, any producer whose signal is genuinely spec-proportioned
(`(white-black)/(black-sync) ≈ 2.5`) decodes correctly with no further
change. Phase 2 makes each producer emit such a signal.

**Verify (with Phase 2 landed together — see Sequencing):**
`EveryHueCodeDecodesCloseToTheReferencePalette` still passes; SMPTE bar tests
unchanged (that signal is already ~2.5 — measured sync ≈ 5, blanking ≈ 76,
white ≈ 255 after `SmpteAsset` normalization); Apple II text still decodes to
pure white, background to black. Pitfall canopy luma drops from near-white
toward `Y ≈ 130`.

## Phase 2 — Every encoder emits the spec scale

Land this in the **same commit** as Phase 1 — between the two the pipeline is
internally inconsistent. This phase only moves reference white to 224 and
nails sync/blanking to 0/64 for each producer; the TIA luma *shape* is
Phase 3.

- **Apple II** (`AppleIISystem.CompositeVideo.cs`). Keep the Gayler voltage
  model untouched (it is real measured hardware). Change only the volts→byte
  map so it is anchored on Gayler's two low landmarks — sync `0 V → 0`,
  blanking `0.5 V → BlankingByte` — i.e. `byte = round(vOut · BlankingByte /
  0.5)`, clamped to 255 (with `BlankingByte = 64`, that is `· 128`). Apple
  II's *measured* white (2.0 V) then lands at `4 · BlankingByte` = 256, i.e.
  ~120 IRE — carried essentially losslessly at byte 255; the real
  consequence is downstream, where `NtscYiqDecoder`'s luma clamp maps it to
  `(255-64)/(224-64)·255 ≈ 304 → 255` and discards the ~20 % excursion above
  reference white.
  - This is faithful, with a caveat. The Apple II genuinely drives white
    ~20 % hot (0.5 V sync-to-black vs 1.5 V black-to-white is a 1:3 split,
    not spec's 1:2.5). A period TV would *not* have hard-clipped that: AGC
    keys off sync, not white, so the excursion passes at full amplitude, and
    what compressed it was soft — beam-current/brightness limiters, CRT
    saturation and blooming, or the viewer turning contrast down. Our hard
    luma clamp is a crude stand-in for that soft top-end compression. For
    1-bit white text it is invisible (white is white either way); the
    content that actually shifts is bright **artifact-colour** pixels whose
    luma+chroma exceeds 224.
  - **Fallback** if the compressed Apple II highlights look wrong in
    practice: scale the map so `2.0 V → ~224` instead (`byte = vOut · 112`),
    i.e. treat the Apple II video DAC as calibrated *to* reference white
    rather than 20 % over it — a defensible reading of the design intent, and
    a one-constant change. Decide by eye against the re-blessed baseline.
  - Burst amplitude (`BurstAmplitudeVolts`) is in the same volts units, so it
    scales with whichever map is chosen — check it still lands near the
    measured 0.7 Vpp.
- **SMPTE** (`SmpteAsset.LoadNormalized`). Its 100 IRE white bar is reference
  white, so change the normalization from `raw · 255 / 200` to `raw · 224 /
  200` so that bar lands on 224. Sync/blanking then follow the asset's own
  ratio (already ~spec). Verify the seven bars' decoded RGB is unchanged
  within tolerance.
- **Space Invaders** (`SpaceInvadersSystem.CompositeVideo.cs`). Change the
  `WhiteLevel` const 255 → 224. The signal is 1-bit (sync / blanking /
  white), so nothing else moves and white still clamps to pure white after
  decode; the const change is just to keep the repo on one scale (and the
  `Channel.Analog` scope range honest).
- **TIA** (`Atari2600System.CompositeVideo.cs`). Change `WhiteLevel` 255 →
  224 now; the `lum/7` line and `ChromaAmplitude` keep working against the
  new constant until Phase 3 replaces them. `SyncLevel` 0 and `BlankingLevel`
  64 are unchanged.

**Verify:** Apple II — text pure white (post-clamp), background black,
artifact-colour hues unchanged (`AppleIISystemTelevisionTests`); re-bless the
Apple II screenshot baseline for the ~20 %-hot-white brightness shift if the
tests carry one. SMPTE bars unchanged. Space Invaders unchanged.

## Phase 3 — Derive the TIA luma curve and chroma amplitude from the output network

Prefer a physical model over a fitted curve. Real hardware turns TIA's
`LUM0/1/2`, `COLOR` (phase-delayed subcarrier), `SYNC` and `BLK` pins into
one composite level through a **passive summing ladder** on the board, whose
node feeds the RF modulator. The luma resistors are deliberately *not* an
R-2R ladder — the chosen values give the compressive curve (steps shrinking
toward white) that is visible directly in `Palette.NtscPalette` row 0
(`Y ≈ 0, 64, 108, 144, 176, 200, 220, 236` for codes 0..7 — not a line).
`lum/7` misses that entirely.

Model:

- Take the ladder resistor values off the 2600 schematic (on AtariAge — note
  the existing memory that some linked schematic PDFs are dead links; the
  schematic itself is findable). Andrew Towers' "TIA Hardware Notes"
  (already in the main TIA plan's reference list) covers the luma DAC and
  colour generation. Encode each resistor as a named constant with its
  source cited, the same way `AppleIISystem.CompositeVideo.cs` documents its
  encoder weights.
- Compute the summing node by superposition:
  `V = Σ(bit_k · Vdrive / R_k) / (Σ 1/R_k + 1/R_bias)`. This yields, from one
  network: the 8 luma levels, `ChromaAmplitude` (the `COLOR` resistor's share
  of the node), and the sync / blanking levels (two more gated inputs) — so
  the whole thing is derived, then normalised onto the Phase 1 byte scale
  (next bullet) rather than three hand-seeded constants.
- **This stays synthesis-side and TIA-only.** It does not touch the decoder
  (Phase 1). The constraints it inherits from Phases 1–2: TIA emits
  `SyncLevel = 0`, `BlankingLevel = 64`, `lum 7` grey at reference white
  `224`, and its emitted `(white - black)/(black - sync)` stays at the spec
  `2.5`. The raw resistor math produces some ratio and absolute scale of its
  own; both are free (the decoder self-calibrates black and sync, and the
  chroma/luma shape is what matters), so apply one overall gain + offset to
  land sync on 0, blanking on 64, and `lum 7` on 224. If the real network's
  own sync:video ratio comes out well away from 2.5, note it — TIA, like the
  Apple II, was not a spec-perfect encoder — but still normalise to 2.5 so
  it shares the decoder's scale.
- **Grey vs coloured sub-range.** TIA's luma DAC does not put chroma-bearing
  hues at the same levels as the greyscale ramp — `lum 0` coloured entries
  sit near `Y ≈ 60` (`$10` = `0x444400`), not at true black. This falls out
  of the network if the `COLOR` pin's own contribution / the DAC path for
  coloured entries is modelled; if the schematic detail isn't recoverable,
  approximate it as a `Col != 0` pedestal (`luma = max(level[lum],
  colourFloor)`, `colourFloor` ≈ the level that decodes `(0, $1)` to
  `0x444400`'s `Y`) and say so in the comment.
- **Free parameter.** The TIA pin drive voltages (LUM/COLOR high/low levels)
  are less well documented than the board resistors — people have scoped
  them (AtariAge teardown threads). Calibrate one scalar `Vdrive` against
  `Palette.NtscPalette` rather than fitting 8 table entries; that keeps the
  model physical with a single named calibration point.
- Precompute the algebra into C# constants (an 8-entry level array +
  `ChromaAmplitude` + the three landmarks) with the derivation in the comment
  block — no run-time resistor solving.
- Keep the `ChromaAmplitude` sync-safety reasoning already in that constant's
  comment (the sine's single low sample per cycle must stay clear of the
  sync-classification midpoint); check the derived value still satisfies it
  and extend the comment rather than dropping the rationale.
- Blanking still forces `Lum = 0 → BlankingLevel` (unchanged); the curve only
  affects active-video `lum`.

**Verify:**
- Grey ramp cartridge (`BuildSolidBackgroundCartridge`, `Col 0`, `lum` 0..7):
  decoded `Y` is a monotonic ramp matching palette row 0 within tolerance.
- `(0, $1)` full-screen decodes to a recognisable dark olive, not black;
  Pitfall trunks likewise.
- Sweep several hues at `lum 3`: decoded U/V magnitudes cluster near the
  corresponding palette entries' magnitudes (complements the existing
  phase-only check).

## Phase 4 — Tests

**Cross-system regression (Phases 1–2).** These guard the scale change:

- **SMPTE bars** — decoded RGB of all seven bars within tolerance of the
  pre-change values (use whatever `NtscYiqDecoderTests` / Television SMPTE
  test already exists as the baseline).
- **Apple II** — `AppleIISystemTelevisionTests`: text luma clamps to white,
  background to black, and Sather's "$2A/$55 → green" artifact-colour landmark
  still decodes green. Update any stored brightness baseline for the
  intentionally-hot Apple II white.
- **Space Invaders** — its Television test (if any) unchanged.
- **Decoder unit** — feed a synthetic spec signal (sync 0, black 64, white
  224, a mid grey at 144) and assert `Luma` comes out ~144 regardless of
  whether a 224 white sample is present in the line: the dim-scene gain
  stability check, at the decoder level.

**TIA (`Atari2600SystemTelevisionTests`)** — it already has the palette-phase
test and `BuildSolidBackgroundCartridge`:

- **Luma ramp:** `Col 0`, `lum` 0..7 each held full-screen; decoded `Luma`
  monotonic and within tolerance of palette row 0's `Y` values (`0, 64, 108,
  144, 176, 200, 220, 236`).
- **Coloured luma-0 is not black:** `(0, $1)` full-screen decodes to
  `Y > ~40` and a non-zero chroma vector.
- **Saturation magnitude:** a mid hue at `lum 3` decodes to a U/V magnitude
  within tolerance of its palette entry (complements the existing
  phase-only check).
- **Dim-scene gain stability, full pipeline:** a two-band cartridge (`lum 6`
  band + `lum 2` band) — the `lum 2` band decodes the same with or without
  the bright band present.
- **Pitfall frame comparison:** fold into the frame-comparison test the main
  TIA plan's Phase 11 already calls for — capture the reference once both
  plans' work has landed.

Keep to the repo test constraints: no full-suite runs, targeted
`--treenode-filter` per touched class, `Aemula.Console --screenshot` for eyeballing.

## Sequencing

1. **Phases 1 + 2 as one commit.** The decoder gain anchor and every
   encoder's move to the 224 reference white are mutually dependent — split
   them and the pipeline is inconsistent in between. Run the full
   cross-system regression set here: SMPTE, Apple II, Space Invaders, plus
   the decoder-level gain-stability unit test. Re-bless the Apple II baseline
   for its intentionally-hot white if needed. Do **not** touch the TIA luma
   shape yet — TIA still runs `lum/7` against the new `WhiteLevel = 224`, so
   its colours shift but stay self-consistent.
2. **Phase 3** — the TIA resistor-network luma curve, colour floor, and
   chroma amplitude. Iterative: the luma floor shifts where chroma rides, so
   re-check saturation after each luma change.
3. **Phase 4 tests** are written alongside each phase, not saved for the end;
   the Pitfall frame-comparison reference is captured last.
