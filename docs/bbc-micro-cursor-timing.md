# BBC Micro: cursor position / video fetch timing investigation

Status: **resolved, uncommitted** (branch `perf/bbc-decode-cache`, on top of
`16f77a3`). Delete this file once the work lands.

## The symptom

At the BASIC prompt the flashing cursor was drawn **under the `>`** instead of
in the character cell after it, in every screen mode.

## Root cause

Two independent defects, which the provisional "ULA applies the cursor one
fetch late" change happened to cancel out in every mode:

1. **The 6845's cursor was out of phase with its other outputs.** The model
   (matched to jsbeeb's internal state) publishes `DispEn`, `HSync` and
   `VSync` one character behind `MA`: after the clock edge that moves MA to
   N+1, they describe character N. That is exactly what the board needs: the
   video ULA (and the IC15 teletext latch) samples the 6845's outputs one 16MHz
   tick after CCLK falls, together with the byte the DRAM read for N, before
   the real chip's outputs (≈100ns+ after the edge) have changed. The
   bbc-vidproc-cpld re-implementation (which runs on real boards) samples
   DISEN and CURSOR at that same edge. `Cursor`, however, compared the *new*
   MA, so it flagged N+1 while the ULA latched N's byte.
2. **The SAA5050 had no internal pipeline**, so Mode 7 text reached the
   screen only one character after the bitmap path. The hardware design
   implies three: Mode 7's R8 cursor skew of 2 plus the ULA's second cursor
   segment (`&4B`) put the cursor three characters after a bitmap-mode cursor,
   and jsbeeb (4-entry data queue, DE delayed by skew + 2), b-em and emu198x
   ("three-clock SAA5050 delay") all agree.

## What changed

- `Motorola6845Chip`: the cursor compare is made in `Tick` in the same step as
  the display enable (on the character the clock ends), and R8 bits 6–7 now
  delay it 0/1/2 characters or turn it off (3). Class doc explains the output
  phase.
- `tools/jsbeeb-harness/crtc-timing.mjs`: the fixture's cursor flag is now the
  pin in DISPEN's phase (jsbeeb's compare of the clock before, delayed by the
  skew), plus three cursor-skew scenarios; fixture regenerated.
- `Saa5050Chip`: two-stage pipeline — F1 shows the cell decoded at the last
  edge, decodes the code taken at the last edge (sampling LOSE then: a cell
  decoded with LOSE low is black, and the first decode after LOSE falls ends
  the scan line), and takes the new code. LOSE coming straight from IC15 is
  then aligned with its code by R8's display skew of 1.
- `BbcMicroSystem.Teletext`: the board-level `_teletextVisible` gate (which
  compensated for LOSE being read a character early) is gone; the SAA5050's
  RGB goes straight to the ULA as on the board.
- The provisional `VideoUlaChip` change was reverted; the ULA is unchanged
  from `16f77a3`.

## Evidence

- `BbcMicroCursorTests`: modes 0–7, the cursor fills exactly one cell (8, 16
  or 32 ticks) and the `>` lies wholly in the cell before it.
- `BbcMicroTeletextTests.TheDisplayedWindowEndsAtTheRowsLastCell`: a row of 39
  solid mosaics followed by a row of solid blocks lights for exactly 624 ticks,
  so LOSE neither overruns nor clips the window.
- Mode 7 minus Mode 4 `>` cell start (ticks after HSYNC): ours 258 − 242 = 16,
  jsbeeb 216 − 202 = 14 (before the fix ours was −16, i.e. Mode 7 text was two
  characters early).
- Release console screenshots of Mode 7 and Mode 1 show the cursor right
  after `>`.

## Residuals (not done)

- bbc-vidproc-cpld delays the cursor inversion by a further three 16MHz ticks
  in teletext mode (`cursor_invert(3)`), to line it up with the SAA5050's own
  output delay after F1; sub-character, not modelled.
- jsbeeb's absolute horizontal placement differs from ours by 8–10 ticks in
  both modes (its origin is only roughly mid-HSYNC); consistent across modes,
  so not chased.
