# TIA — Object, Priority, Collision, Audio Completion Plan

## Goal

Bring `TiaChip` from "playfield + two players" to a complete TIA: missiles,
the ball, object priority / score mode, vertical delay, collision latches,
input ports, full HMOVE, and audio. Success is measured against real ROMs
rendered through `Aemula.Console --screenshot`: **Pitfall's tree trunks,
ladders and hazards render correctly**, and a small suite of TIA unit tests
plus a Pitfall frame-comparison test pass.

This plan does **not** touch the composite-video / `Television` path
(`Atari2600System.CompositeVideo.cs`) — that landed already and is correct.
It also keeps `TiaChip.Col` as a 4-bit hue index (see that field's own doc
comment for why). Everything here is about what TIA puts on `Lum`/`Col`
*before* the composite stage samples them.

## Current state

Audited against `src/Aemula/Emulation/Chips/Tia/` at the time of writing.

**Works:** horizontal LFSR counter, HBLANK/HSYNC, WSYNC/RDY, RSYNC, VSYNC,
VBLANK, playfield (PF0/PF1/PF2, `COLUPF`/`COLUBK`, reflect flag, score flag),
players 0/1 (`GRP`, `COLUPx`, `REFPx`, `RESPx`, `NUSIZx` copies, `HMPx`,
`HMOVE`, `HMCLR`), colour burst, Phi0 ÷3, Phi2-gated register writes.

**Missing entirely** (empty `case` bodies in `TiaChip.Phi2`):

| Feature | Registers |
| --- | --- |
| Missiles | `ENAM0/1`, `RESM0/1`, `HMM0/1`, `RESMP0/1`, `NUSIZ` missile-width bits |
| Ball | `ENABL`, `RESBL`, `HMBL`, `CTRLPF` D4–D5 (size), `VDELBL` |
| Playfield priority | `CTRLPF` D2 (PFP) |
| Vertical delay | `VDELP0`, `VDELP1`, `VDELBL` |
| Collisions | `CXM0P`…`CXPPMM` reads, `CXCLR` |
| Input ports | `INPT0`…`INPT5` reads |
| Audio | `AUDC0/1`, `AUDF0/1`, `AUDV0/1`; `Aud0`/`Aud1` pins never driven |

**Partial / suspect:**

- `TiaChip.Phi2`'s `RW` (read) branch is a stub — it never drives `Data67`,
  so *no* TIA register is currently readable.
- `PlayerAndMissile.ExecutePlayerLogic` only decodes a subset of `NUSIZ`;
  double- and quad-width players (`NUSIZ` 5 and 7) are not scaled.
- `DoPlayfield` composites objects by "last writer wins" (`P1.DoPlayer`
  overwrites `P0.DoPlayer` overwrites playfield) — wrong order, and no basis
  for adding more objects or collisions.
- `DoPlayfield` carries `// TODO: Reflect playfield`; `_playfieldIndex` is
  advanced by `++` with magic-number fix-ups (`0x14`, `2`, …) at HBLANK /
  center / late-reset states.
- HMOVE (`Osc` setter) gives extra clocks to **players only**; no missile /
  ball motion, no left-edge comb.
- `ExecuteClockLogic` has `// TODO: Tick audio` at counter state `0b010100`.

**Tests:** none for TIA (`Aemula.Tests/Emulation/Chips/` has TTL parts, 6502,
8080, 2A03 only). The Atari2600 system tests do no pixel/frame comparison.

## Reference materials

- [Stella Programmer's Guide](http://atarihq.com/danb/files/stella.pdf) —
  register map, priority table, NUSIZ / CTRLPF / VDEL / HMOVE semantics.
  Primary reference for every phase.
- [Andrew Towers, "TIA Hardware Notes"](http://www.atarihq.com/danb/files/TIA_HW_Notes.txt)
  — gate-level detail on the object counters, HMOVE comparator, the HMOVE
  comb, score/priority muxing. Matches the control-line names already used
  in `ExecuteClockLogic` (Towers' "Reset HSYNC", "RCB", etc.).
- [Nocash 2600 specs](https://problemkaputt.de/2k6specs.htm) — concise
  collision-register bit table, INPT bit meanings, HMxx signed encoding.
- [Gopher2600 Video Cycle Timeline](https://raw.githubusercontent.com/JetSetIlly/Gopher2600-Dev-Docs/master/src/Video_Cycle_Timeline/Gopher2600%20Video%20Cycle%20Timeline.svg)
  — per-colour-clock timing chart; cross-check object start offsets and the
  HMOVE-extended HBLANK against this.
- [Stella source](https://github.com/stella-emu/stella/tree/master/src/emucore)
  (`TIA*.cxx`, `Player.cxx`, `Missile.cxx`, `Ball.cxx`, `Playfield.cxx`,
  `Audio*.cxx`) — reference implementation for behaviour not pinned down by
  the prose sources, especially audio polynomial taps.

## Fidelity approach

Match the level `TiaChip` already sits at: **cycle-accurate at the colour
clock, register-accurate, but not gate-level.** Objects are modelled as
"counter + decode points + graphic shift", the same shape as the existing
`PlayerAndMissile`, not as transistor netlists. This is consistent with
`Mos6502Chip` / `Ricoh2C02Chip` in this repo (cycle/state accurate, not
gate-level) and with the existing TIA code.

Deliberately **out of scope** (call them out in code where relevant, don't
half-build them):

- The HMOVE bug (strobing HMOVE late in the line, near colour clock 74,
  producing partial motion + a ragged comb). Note it at the HMOVE site;
  revisit only if a target ROM needs it.
- Paddle / pot RC-timing on `INPT0-3` — no analog paddle model exists.
  Return the dumped-to-ground state only.
- Real analog `Col` phase output — unchanged, see `TiaChip.Col`'s comment.
- Startup / power-on randomness of object positions.

**Comment discipline:** per repo convention, planning docs are deleted once
their work lands — so no code comment may cite "Phase N" or this file. Bake
the *reason* into the comment (the way `_colorBurst`'s remarks already do).

## Phase 1 — Compositing pipeline

Foundational; everything after depends on it.

Replace the "each object writes `tia.Lum`/`tia.Col` in turn" scheme with a
two-step-per-colour-clock model:

1. **Each object reports a boolean** "my pixel is on here" for the current
   colour clock: `P0`, `M0`, `P1`, `M1`, `PF`, `BL`. Players/missiles/ball
   already have (or will have) the per-clock graphic bit; playfield computes
   its bit from horizontal position (see Phase 8).
2. **A single resolver** picks the winning object by priority and writes
   `Lum`/`Col` once, from that object's colour register:
   - Normal: `P0/M0` → `P1/M1` → `BL/PF` → `BK`.
   - `CTRLPF` D2 set: `PF/BL` → `P0/M0` → `P1/M1` → `BK`.
   - Score mode (`CTRLPF` D1, ignored when D2 set): a *set* playfield bit
     takes `COLUP0` in the left half of the screen, `COLUP1` in the right
     half; the ball keeps `COLUPF`.
3. Blanking still forces `Lum = 0`, `Col = _colorBurst ? 1 : 0` last, as
   today.

Keep the object bit-getters cheap (no allocation) — this runs every colour
clock. Store the six bits in locals, not a collection.

`PlayerAndMissile.DoPlayer` stops writing `tia.Lum/Col` directly; it exposes
the current player graphic bit instead. `DoPlayfield` becomes
`DoVideo` (or similar) and owns the resolver.

**Verify:** existing Pitfall/other screenshots are unchanged for scenes with
no object overlap (the only case the old order got wrong).

## Phase 2 — Missiles

A missile is a one-object degenerate player:

- **Counter & copies:** reuse the player's LFSR + `NUSIZ` D0–D2 copy decode,
  so missile copies line up under player copies. Width from `NUSIZ` D4–D5:
  1 / 2 / 4 / 8 colour clocks.
- **`ENAM0/1` (0x1D/0x1E):** D1 enables the missile graphic.
- **`RESM0/1` (0x12/0x13):** reset the missile counter (same +4/+5 start
  offset handling as `RESP`).
- **`HMM0/1` (0x22/0x23):** motion register, same signed encoding as `HMPx`
  (store bit 3 inverted, matching the existing `HMPx` trick).
- **`RESMP0/1` (0x28/0x29):** D1 locks the missile to its player's centre
  and suppresses its display while set; on clear, the missile resumes from
  that position.
- **Colour:** `COLUP0` / `COLUP1` (shared with the player).

Implementation: give `PlayerAndMissile` a `Missile` sub-struct (counter,
enabled, width, `_resmp`), or add the fields directly — it's already named
for this. Missile bit feeds the Phase 1 resolver at `M0`/`M1` priority.

**Verify:** Pitfall tree trunks now render as solid vertical bars in the
`COLUP` colour (trunks are missiles held enabled across the canopy rows).

## Phase 3 — Ball

- **State:** own LFSR counter, `enabled`, `size` (`CTRLPF` D4–D5 → 1/2/4/8),
  no copies.
- **`ENABL` (0x1F):** D1 enables (subject to `VDELBL`, Phase 5).
- **`RESBL` (0x14):** reset counter.
- **`HMBL` (0x24):** motion, same encoding as the others.
- **Colour:** always `COLUPF` (even in score mode).
- **`CTRLPF` (0x0A):** finish parsing this register — today only D0 (reflect)
  and D1 (score) are read; add D2 (priority, Phase 1) and D4–D5 (ball size).

Ball bit feeds the resolver at `BL` priority (grouped with `PF`, order per
`CTRLPF` D2).

**Verify:** any ROM that draws a ball (e.g. Combat's shots, a homebrew
ball-test) shows it at the right x, width and priority.

## Phase 4 — Priority & score mode polish

Phase 1 introduces the resolver; this phase pins down the awkward cases:

- Score mode + priority bit interaction (priority wins, score ignored).
- Score-mode left/right split point is screen-centre (colour clock 80 of the
  160 visible), independent of playfield reflection.
- Playfield/ball as one priority group but **distinct colours** (ball =
  `COLUPF`, playfield = `COLUPF` or score colours).
- Collision detection (Phase 6) samples *object presence*, which is
  priority-independent — make sure Phase 1 exposes the raw bits, not just
  the resolved winner.

**Verify:** a priority/score test ROM (e.g. from the AtariAge "emulator test
programs" thread) renders each region with the documented colour.

## Phase 5 — Vertical delay

Add the dual graphics latches:

- Player 0: `GRP0A` (delayed) + `GRP0B` (displayed). `VDELP0` (0x25 D0)
  selects which a `GRP0` write lands in. A `GRP1` write copies
  `GRP0A → GRP0B`.
- Player 1: symmetric; a `GRP0` write copies `GRP1A → GRP1B`.
- Ball: `ENABLA` + `ENABLB`; `VDELBL` (0x27 D0). A `GRP1` write copies
  `ENABLA → ENABLB`.

Display reads the `*B` latch always; the `*A` latch only receives writes
when the matching `VDEL` bit is set.

**Verify:** a two-line-kernel sprite ROM (very common) shows no vertical
tearing; Pitfall Harry's animation is stable.

## Phase 6 — Collisions

- **15 latches** as a bitfield. Each colour clock, from the six raw presence
  bits (Phase 1/4), set the latch bit for every colliding pair that is
  currently overlapping. Latches are sticky until cleared.
- **Reads** (`TiaChip.Phi2` `RW` branch): decode `Address & 0x3F` in
  `0x30`–`0x37`, return the pair bits on **D7/D6** via `Data67` (the read
  branch must actually drive `Data67` now — today it does nothing). Bit
  layout per Nocash / Stella:

  | Addr | Reg | D7 | D6 |
  | --- | --- | --- | --- |
  | 0x30 | CXM0P | M0∩P1 | M0∩P0 |
  | 0x31 | CXM1P | M1∩P0 | M1∩P1 |
  | 0x32 | CXP0FB | P0∩PF | P0∩BL |
  | 0x33 | CXP1FB | P1∩PF | P1∩BL |
  | 0x34 | CXM0FB | M0∩PF | M0∩BL |
  | 0x35 | CXM1FB | M1∩PF | M1∩BL |
  | 0x36 | CXBLPF | BL∩PF | (unused) |
  | 0x37 | CXPPMM | P0∩P1 | M0∩M1 |

  D5–D0 are open bus — leave the existing `_cpu.Data & 0x3F` bits from the
  bus in place (the system already ORs `Data67 << 6` onto them).
- **`CXCLR` (0x2C):** clear all latches.

**Verify:** a collision test ROM; Pitfall Harry falling into the pit / being
stopped by the log behaves correctly (needs P1∩PF, P0∩BL etc.).

## Phase 7 — Input ports

`TiaChip.Phi2` `RW` branch, `Address & 0x3F` in `0x38`–`0x3D`, result on
**D7** via `Data67`:

- **`INPT0`–`INPT3` (0x38–0x3B):** dumped inputs. With no analog paddle
  model, return D7 = 0 while `_i03DumpToGround` is set, otherwise reflect the
  corresponding `I` bit (I0–I3).
- **`INPT4`/`INPT5` (0x3C/0x3D):** latched trigger inputs. When
  `_i45Enable` is clear, D7 = current `I` bit (I4/I5). When set, D7 latches
  low on a low pin level and holds until `_i45Enable` is cleared.

Wire `Atari2600System.DoAddressDecode` so the read path ORs TIA's `Data67`
onto the bus for these addresses (the collision-read wiring from Phase 6
already covers the mechanism).

**Verify:** a joystick-fire ROM reads INPT4/5; existing RIOT-based joystick
direction reads are unaffected.

## Phase 8 — HMOVE completeness & playfield cleanup

**HMOVE:**

- Extend the existing comparator (`_hmoveComparator`, `NoneEqual`) to
  missiles and the ball — each gets its own HM register + latch, ticked in
  the same div-4 block as the players are today.
- **Comb / extended HBLANK:** when `HMOVE` is strobed during HBLANK, hold
  `HBLANK` for an extra 8 colour clocks into the visible line, so the
  leftmost 8 pixels are border-black. This is the same "late Reset HBLANK"
  path the `0b010111` counter state already half-implements — unify them.
- Note (don't fix) the late-strobe HMOVE bug at the strobe site.

**Playfield:** resolve `// TODO: Reflect playfield`. Prefer recomputing the
active PF bit from the current horizontal position each colour clock
(`position 0..79` → PF bit index, mirrored for the right half when
`CTRLPF` D0 set) instead of the running `_playfieldIndex++` with
magic-number fix-ups. Verify the canopy shape in Pitfall matches the
reference (rounded lobes, not a scalloped block).

**Verify:** an HMOVE stress ROM (e.g. "Player/Missile move test") positions
objects to the exact documented pixel; the 8-pixel comb appears when
expected.

## Phase 9 — Audio

Lowest priority; no target ROM here needs it for *video* correctness, but
it completes the chip.

- Two channels. Each: `AUDF` (0x17/0x18, 5-bit divider), `AUDC`
  (0x15/0x16, 4-bit waveform / poly-tap select), `AUDV` (0x19/0x1A, 4-bit
  volume).
- Clock each channel twice per scanline (Towers: at HBLANK start and at
  centre — replace the `// TODO: Tick audio` at `ExecuteClockLogic` state
  `0b010100` and add the second tick point).
- Poly-counter waveform tables per `AUDC` from Stella's `AudioChannel.cxx`.
- Output: 1-bit waveform × `AUDV` → drive `Aud0` / `Aud1`. If a numeric
  sample is wanted later, sum the two channels — but keep the pins as the
  primary output to match the existing pin model.

Model as a `TiaAudioChannel` struct owned by `TiaChip` (mirrors
`PolynomialCounter` living beside `TiaChip`).

**Verify:** frequency of a known tone (e.g. a game's menu blip) matches
`AUDF`/`AUDC` math; no target regression.

## Phase 10 — NUSIZ player scaling

Fold in with Phase 2 if convenient, otherwise standalone. Handle all 8
`NUSIZ` D0–D2 values in `ExecutePlayerLogic`:

- Copies: 1 / 2-close / 2-med / 3-close / 2-wide / 3-med (values 0–4, 6).
- Double (5) / quad (7): advance `_scanCounter` every 2nd / 4th colour clock
  instead of every clock, so the 8-bit graphic stretches ×2 / ×4.

**Verify:** a NUSIZ test ROM shows all six copy layouts and the two stretch
modes at the right widths.

## Phase 11 — Tests

Per repo constraints: no full-suite runs (~40 min), no manual UI launching —
use `Aemula.Console --screenshot` and targeted `--treenode-filter` runs.

- **New `Aemula.Tests/Emulation/Chips/Tia/`** (folder, since it needs a
  small harness that clocks `Osc`/`Phi2` and reads pins):
  - HMOVE motion amount per HMxx value (all 16), including comb width.
  - `NUSIZ` copy positions and stretch widths.
  - Missile / ball `RESx` + `HMxx` final position.
  - `VDELP0/1/BL` old/new latch copy timing.
  - Collision latch set/clear for representative pairs.
  - Priority resolver: normal vs `CTRLPF` D2, plus score mode.
  - Playfield reflect symmetry.
- **`Atari2600SystemTests`**: a Pitfall frame-comparison test — run N frames
  headless, hash or compare `Television` output (or the pre-composite
  `Lum`/`Col` raster) against a committed reference captured once this plan
  is complete. Same spirit as `AppleIISystemTelevisionTests`.
- Keep `docs/` reference screenshots (our render vs. the wiki reference) out
  of the repo; regenerate via `Aemula.Console` when needed.

## Suggested sequencing

Phase 1 unblocks everything and must land first. Then the highest visual
payoff for the motivating case (Pitfall) is **2 → 3 → 4 → 5 → 8**. Phases
6 (collisions) and 7 (inputs) are gameplay-correctness, not rendering, and
can slot in any time after Phase 4. Phase 9 (audio) and Phase 10 (NUSIZ
scaling) are independent and can land whenever. Phase 11 tests are written
alongside each phase, not saved for the end.
