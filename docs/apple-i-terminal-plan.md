# Apple I Terminal Section — Implementation Plan

## Status and relationship to `apple-i-plan.md`

[apple-i-plan.md](apple-i-plan.md) got the system running WozMon end-to-end
(CPU/memory/PIA/video-timing chain), but its own phase 4 "done when" criterion
— "you can see and type at the WozMon `\` prompt" — isn't met yet. The
current `AppleISystem.CharacterMemory.cs` approximates the write-cursor state
machine with a single `_pendingWrite` bool and a hand-waved cursor-advance;
that approximation loses the very first character WozMon ever echoes (see the
investigation that prompted this doc) and has no carriage-return or scroll
behaviour at all.

This plan replaces that approximation with the **actual gate-level circuit**,
now that a full netlist is available (see "The netlist" below) instead of
having to read chip pin positions off a rendered PDF tile by tile. Treat this
doc as phase 4's replacement, not an addition — once it lands,
`apple-i-plan.md`'s phase 4 section should be marked superseded by this one.

## The netlist

[`apple-i-netlist.txt`](apple-i-netlist.txt) — extracted by Claude (a
separate session) directly from the same
[hansotten PDF](http://retro.hansotten.nl/uploads/apple1/a1%20circuit.pdf)
`apple-i-plan.md` cites, sheets 1 (terminal) and 2 (processor). Format is
documented in its own header: `COMPONENT` lines give designator/type/sheet,
`NET` lines list every pin on each named net, auto-generated names start with
`_S<sheet>_<id>` for unlabelled nets the tool had to synthesize its own name
for.

**This is now the primary source for the terminal section** — more reliable
than re-reading the PDF tile-by-tile (which is how the chip-inventory-level
mistake below happened), and it's what the equations in this doc were
resolved from, pin-by-pin, cross-checked against real datasheet pinouts
(TI/Fairchild/Signetics) rather than assumed. The resolution method (map each
component's type to its real pinout, substitute net names, catch
inconsistencies like an output pin ending up on a net with another output —
that's how the 2504 IN/OUT swap below was caught) is mechanical and worth
redoing wholesale in code/a script rather than by eye if any further nets
need tracing.

## Corrections to `apple-i-plan.md`'s chip inventory

- **The seventh 2504 is real** — `apple-i-plan.md`'s original inventory
  (`ICC11B` as the cursor's own recirculating bit) was right. An early cut
  of the generated netlist mistyped it as a second `DS0025` (and, worse,
  wrote both `ICC11A`/`ICC11B` with the section-colon notation used for
  multi-gate packages like `ICC5:A`/`:B`/`:C`, implying they were two
  channels of one physical part). Neither was true: **`ICC11A` and `ICC11B`
  are two separate physical chips** (no colon, per the schematic — same
  convention as `ICD4A`/`ICD4B` etc.), `ICC11A` is the `DS0025` clock
  driver, and `ICC11B` is a genuine 2504, the cursor register — confirmed
  and corrected in `apple-i-netlist.txt`. `AppleISystem.CharacterMemory.cs`'s
  existing `_cursorBit = new Signetics2504Chip()` design is architecturally
  correct as-is; what it's missing is the real set/clear wiring (below), not
  a redesign of the storage.

  Resolved wiring for `ICC11B` (2504): `IN` (pin 5) = `_S1_93` =
  `AND(pin 11, /WC2)` (`ICC12:C`); `OUT` (pin 1) = `_S1_55`, which feeds
  pin 13 (a D input) and from there pins 14/15 (its `/Q`/`Q`) — **`CURS` is
  pin 14**, i.e. "cursor bit was present here as of the last sample", not
  the cursor 2504's raw `OUT` directly. So `CURS` always lags the actual
  cursor-ring position by the propagation through `ICC13`'s own clock (see
  the `ICD15` entry below for what that clock is) — worth preserving that
  one-step indirection in the C# port rather than reading `_cursorBit.Out`
  straight into `CURS`-consuming logic, since the two aren't quite the same
  signal.

  In `Ttl74175Chip` terms (that class numbers its four flip-flops
  `D1`–`D4`/`Q1`–`Q4`/`Qn1`–`Qn4`, 1-indexed, one higher than the
  schematic's own `D0`–`D3`/`Q0`–`Q3` silkscreen — same off-by-one as
  `ICC7`/`Ttl74174Chip` above, and the exact mismatch you caught when
  checking the first draft of this doc against the PDF): pin 11 is `Qn3`,
  pin 12 is `D3`, pin 13 is `D4`, pin 14 is `Qn4`. So: `ICC11B.IN =
  AND(icc13.Qn3, /WC2)`, `icc13.D4 = ICC11B.OUT`, `CURS = icc13.Qn4`. Pin
  numbers are the unambiguous reference if this needs rechecking again;
  treat any bare "`Qn`-something"/"`Q`-something" name in this doc as
  `Ttl74175Chip`/`Ttl74174Chip`'s own 1-indexed property names, not the
  schematic's silkscreen.

- **`ICD15`** (74161, one of the counter bank): clock is `CLA` — confirmed
  by you as literally the character-rate clock, same signal `ICD6`–`ICD9`
  and `ICD11` all share — and its count-enables (`CET`/`CEP`, i.e. `ENT`/
  `ENP`) are both tied to `LASTH` (`ICD7`'s `RCO`, "last horizontal" /
  end-of-line). So **`ICD15` only advances once per line** (enabled for
  exactly the one character-time where `LASTH` is asserted), and its `QB`
  output (pin 13, called `Q1` in datasheets that number outputs `Q0`–`Q3`
  instead of `QA`–`QD`) is `VINH`. That confirms the phase-4a design below:
  `ICD8`/`ICD9`'s count-enable being tied to `VINH` really does gate them to
  "advance only during blanking, held during the 40 active characters",
  driven by a counter that itself only ticks once per line — consistent,
  no further ambiguity here.

- **The 2504's `IN`/`OUT` pin assignment in any hand-derivation must be
  pin 5 = `IN`, pin 1 = `OUT`** (confirmed by tracing bus conflicts in the
  netlist — the reverse assumption puts two live outputs on one wire). Not a
  code-visible issue (`Signetics2504Chip.cs` already exposes `In`/`Out` as
  named properties, not raw pin numbers) but worth stating for anyone
  re-deriving nets by hand later.

- **`ICB3:B`, the 3.5 µs write-acknowledge one-shot, lives on the processor
  sheet (sheet 2), not the terminal sheet.** Its trigger (`/RDA`, pin 9)
  comes from `ICC7`'s pin-7 output (`Q3` in `Ttl74174Chip` terms) — i.e. the
  terminal section's state machine
  reaches across sheets to fire it. `AppleISystem.cs` will need a
  `Ttl74123Chip` instance wired from `AppleISystem.CharacterMemory.cs`'s
  logic even though the real one-shot is drawn on the processor page.

## Resolved signal equations

These are the spec to implement against, not the implementation itself —
same as every other chip in this codebase, each named gate below gets
wired up as a real `Ttl74xxChip`/`Signetics25xxChip` instance with its pins
set from other chips' pin properties (exactly how `AppleISystem.cs` already
wires `Ttl74154Chip`, `Mos6820Chip`, etc.); a boolean expression baked
directly into a line of C#, skipping the chip instances, isn't gate-level
fidelity even if it computes the same answer today.

Net names below are the schematic's own labels (leading `/` = active-low, as
in the netlist). Gate designators refer to the real pinout-resolved role,
not just "some gate on that package" — cross-check against
`apple-i-netlist.txt` directly if extending this.

**Display-data bus.** `RD1`–`RD7` = PIA `PB0`–`PB6` (pins 10–16 of `ICA4`),
confirmed directly (no ambiguity here, unlike the plan's original "which PIA
bits" hedge). `DA` = `PB7` (pin 17) — the busy/ready flag WozMon's `ECHO`
polls.

**`CLR`** (`ICC9:A`, 7432 OR): `CLR = VBL OR B4.12`. `B4.12` is the **CLEAR
SCREEN** key on the keyboard connector — a dedicated key, not a PIA data
line, exactly as `apple-i-plan.md`'s keyboard section implies but doesn't
spell out. `CLR` drives `ICC4`/`ICC14`'s `/E` (output-disable) pins directly,
forcing character code `$00` into the write path whenever it's asserted.
**`$00` → `@` on this ROM is the real, correct "blank" state** — there is no
hardware path that ever forces `$20` (space) into the rings. The screenful of
`@` after a real clear (or, currently, after emulated reset) is accurate;
don't special-case it away.

**`WRITE`** (`ICC9:D`, 7432 OR): `WRITE = _S1_89 OR NAND(CURS, DA_delayed)`,
where `DA_delayed` is `DA` registered one `MEM0` clock (`ICC7` pin 4→5,
`D2`→`Q2` in `Ttl74174Chip` terms).
This feeds `ICC4.S`/`ICC14.S` (the write-mux select) directly — so unlike
the current code's single `_pendingWrite && cursorHere` bool, the real select
line depends on a *registered, one-clock-delayed* view of `DA`, not the live
value. That extra clock of latency is almost certainly what makes the timing
forgiving enough for WozMon's busy-poll to actually see it (see the write-
acknowledge chain below).

**CR (`$0D`) decode**: `ICC6:C` (7410 NAND) computes `NAND(RD1, RD3, RD4)`;
`ICC5:C` (7427 NOR) combines that with `RD5`/`RD2` into a signal true only
for byte values with `RD1=RD3=RD4=1, RD2=RD5=0` (a 4-value family that
includes `$0D`/CR, `$2D`/`-`, `$4D`/`M`, and an unreachable `$6D` given the
uppercase-only keyboard) — resolved to CR specifically one AOI stage later in
`ICC8:B`, combined with `/WC2` and the write-pending signal. **Needs a real
`Ttl7450Chip` instance wired pin-for-pin for `ICC8:B`**, same as every other
gate in this doc and the same way `AppleISystem.CharacterMemory.cs` already
wires `Ttl74157Chip`/`Signetics2504Chip`/etc — not a hand-rolled boolean
shortcut that skips the chip. The structure is now known; what's left is
wiring `Ttl7410Chip`(`ICC6:C`) → `Ttl7427Chip`(`ICC5:C`) → `Ttl7450Chip`
(`ICC8:B`) as actual chip instances and unit-testing *that wiring* against
all four byte values sharing those bits (`$0D`/CR, `$2D`, `$4D`, and the
unreachable `$6D`), not just `$0D` — the whole point of gate-level fidelity
here is that the test exercises the real gates, the same way
`AppleISystemCharacterMemoryTests` already does for the write mux.

**Write-acknowledge chain** (`ICC7`, 74174, clocked by `MEM0`). Pin numbers
are the schematic's own (and the netlist's); the `Ttl74174Chip` column is
what to actually set/read in code — **that class numbers its six
flip-flops `D1`–`D6`/`Q1`–`Q6` (1-indexed)**, one higher than the
schematic's own `D0`–`D5`/`Q0`–`Q5` silkscreen, so every row below is
shifted by one relative to a naive reading of the schematic's own labels —
worth restating explicitly since this is exactly the kind of off-by-one
that survived one full derivation pass already (see the `ICC13` correction
below):

| Pin (D / Q) | `Ttl74174Chip` | D input | Q output | Meaning |
|---|---|---|---|---|
| 3 / 2 | `D1`/`Q1` | `LAST` (`ICD9.RCO`) | `_S1_101` | registered end-of-frame |
| 4 / 5 | `D2`/`Q2` | `DA` (PIA `PB7`) | `_S1_148` | registered busy flag |
| 6 / 7 | `D3`/`Q3` | `_S1_71` = `NAND(CURS, _S1_148)` | `/RDA` | drives `ICB3:B`'s one-shot trigger (sheet 2) → the real CB1 pulse |
| 11 / 10 | `D4`/`Q4` | `_S1_85` (CR/write-pending combiner) | `_S1_88` | |
| 13 / 12 | `D5`/`Q5` | `LASTH` (`ICD7.RCO`) | `_S1_105` | registered end-of-line |
| 14 / 15 | `D6`/`Q6` | `/WC1` | `/WC2` | **the literal "kill cursor now, set one clock later" register** |

The last row (`D6`→`Q6`) is the concrete form of Chris Espinosa's
"flip-flops 2 and 3 at C13 re-set the cursor bit on the next character
clock" — except it's `ICC7`'s own flip-flop, feeding `ICC13` (see below),
not `ICC13` doing the delay itself.

**`/WC1`** (`ICC12:B`, 7408 AND): `/WC1 = WRITE AND NOT(_S1_169)`, where
`_S1_169` (`ICC8:A`, 7450 AOI) = `NOT( (_S1_85 AND _S1_105) OR (ClearKey AND _S1_101) )`
— i.e. `/WC1` fires on a normal write-accept gated by "not already at
end-of-line", OR is suppressed/redirected near end-of-frame when the clear
key is held. This is the scroll/CR-repeat trigger point; **the exact
end-of-line/end-of-frame interaction needs to be worked through with truth
tables during implementation**, not just transcribed as one line of C# — get
this wrong and CR-to-next-line or scroll will silently misfire only near
line/frame boundaries, which is exactly the kind of bug that survives a quick
manual test.

**Row/blanking counter interlock** (`ICD8`/`ICD9`, the "`V0`–`V5`" counter):
clocked at the character rate (`CLA`, shared with the horizontal counter
`ICD6`/`ICD7`), but its count-enable (`ENP`) is tied to `VINH` — confirmed
as `ICD15`'s `QB` output, where `ICD15` itself only advances once per line
(its own `CET`/`CEP` gated by `LASTH`, `ICD7`'s end-of-line `RCO`) — which
holds `ICD8`/`ICD9` frozen during the 40 active characters of a line and
only lets them advance during blanking. `V0`–`V2`
feed `ICD2` (2513) `A1`–`A3` (row-within-glyph) directly — confirmed by the
matching net names on both chips — and separately feed `ICB2:B` (7410 NAND)
producing `NAND(V0,V1,V2)`, which is `ICC3` (2519)'s `Recirculate` pin: true
except when `V0=V1=V2=1` (count 7), i.e. **the line buffer only loads a
fresh row on the last scanline of each character row** — this is exactly
what `AppleISystem.CharacterMemory.cs`'s current `TickLineBufferClock`
already implements (`Recirculate = (VerticalCount % 8) != 7`); that part of
the current code survives this rewrite unchanged, just needs re-deriving
from the real `V0`-`V2` counter instead of reusing `VerticalCount`.

`ICD8`/`ICD9`'s `/LOAD` is tied to `OR(/VBL, /WC1)` (`ICC9:C`) — i.e. this
counter reloads to its preset exactly when **both** `VBL` and the `/WC1`
write-accept condition are true simultaneously. This is the scroll trigger:
`apple-i-plan.md`'s own guess ("gated by a `/WC1`-and-`/VBL` condition") was
directionally right, just needed the actual gate to confirm the active
senses line up as AND-of-actives once the active-low naming is unwound.

**Cursor blink**: `ICD13` (555, free-running per `R10`/`R11`/`C7`) → `ICC12:A`
(7408 AND, gated by `_S1_139`, itself `AND(555-out, _S1_96)` where `_S1_96`
comes off `ICC14`'s spare mux channel as `WRITE AND CURS`) → `ICC10:A`
(7402 NOR, combined with the RD7-plane's live write data) → lands on `ICC3`
(2519) pin 3, one of its six character-input pins. Confirms the blink
modifies what gets **written** into that specific bit-plane's line-buffer
input at the moment it's live, not a separate video-output-stage XOR — i.e.
it's part of the same data path as the other five planes. `CURS` here is
`ICC13`'s `/Q4` per the corrected cursor wiring above, not the cursor
2504's raw `OUT`.

## Phased implementation plan

**Phase 4a — Frame-locked ring clock.** Replace `TickCharacterMemory`'s
unconditional per-character-time `PulseCharacterMemoryClock()` with the real
gating: all seven 2504s (the six character planes plus `ICC11B`, the cursor
register) shift on `O3`/`O4`, driven by `ICC11A` (the `DS0025` clock driver)
off the same `CLA`/blanking-gated timing as `ICD8`/`ICD9`, not every
character-time unconditionally. Rebuild
`AppleISystem.VideoTiming.cs`'s vertical counter around the real `V0`–`V5`
role (row-within-glyph + blanking-gated advance) instead of the current
free-running mod-256 stand-in. **Done when**: existing
`AppleISystemVideoTimingTests` still pass and a new test confirms the ring
completes exactly one full effective rotation per detected frame.

**Phase 4b — Real write/DA handshake.** Replace `_pendingWrite`/`Pia.PB`'s
per-character-time update with a real `Ttl74174Chip` instance for `ICC7`,
wired pin-for-pin (its `D2`→`Q2` registers `DA`, feeding `_S1_71` and on into
`D3`→`Q3` = `/RDA`, per the table above), and the real `ICB3:B` one-shot
(`Ttl74123Chip`, already in the chip library) firing `CB1` off `/RDA`.
**Done when**: a test
reproduces WozMon's reset-time `\` + CR echo and both land at distinct ring
positions (this is the regression test for the bug that started this whole
investigation — today only the CR survives).

**Phase 4c — CR decode and write-cursor advance.** Implement the full
`ICC6:C`/`ICC5:C`/`ICC8:B` CR-detect chain, `ICC7`'s `D6`→`Q6`
(`/WC1`→`/WC2`, the kill-now/set-one-clock-later register), and `ICC11B`'s
real set/clear wiring through `ICC12:C`/`ICC13` (its `Qn3`/`D4`/`Qn4`, per
"Corrections" above) — keeping
`_cursorBit` as a real `Signetics2504Chip`, just driven by the actual gates
instead of `_cursorSetPending`. **Done when**: typing a full line and
pressing Return lands the cursor at column 0 of the next line, with the
skipped columns filled by real blanks (`$00`, not stored CR).

**Phase 4d — Scroll.** Implement the `ICD8`/`ICD9` `/LOAD` = `/VBL·/WC1`
reload as the scroll trigger. **Done when**: filling the screen and
continuing to type scrolls the display up one line per the plan's original
phase-4 acceptance criterion ("typing near the right and wrapping to a new
line needs to actually work").

**Phase 4e — Cursor blink.** Wire `Ne555Chip` (already in the library)
through `ICC12:A`/`ICC10:A` into the character-input path. Cosmetic; do
last.

**Phase 4f — Console typing harness.** Give `AppleISystem` a way for
`Aemula.Console --input` to drive the keyboard, CLEAR SCREEN, and RESET keys
headlessly (today only `OnKeyEvent`, called from the UI, can reach the
keyboard at all) — needed to screenshot-test any of the above without the
UI.

## Open items — verify before/while implementing

- **`ICC8:A`/`ICC8:B` (7450 AND-OR-INVERT) pin roles** were derived
  structurally (matching NC-pin gaps against the netlist's own
  per-component pin lists) rather than read off a datasheet picture of a
  7450 specifically. High confidence (the derivation was self-consistent
  and cross-checked against the expander-pin convention 7450 datasheets
  document), but a quick sanity check against the PDF's `ICC8` symbol
  wouldn't hurt before phase 4b/4c lock in its equations in code.
- **Exact `H4`/`H5`/`H6` → horizontal-count-value mapping** (i.e. the
  precise HSYNC/active-window thresholds) wasn't fully pinned to specific
  counter values — the *structure* (`ICD6` BCD ones-digit cascaded into
  `ICD7`'s tens-digit, reload gated by `ICD7.RCO` through `ICD12:F`) is
  confirmed and already matches what `AppleISystem.VideoTiming.cs` does
  today, but the exact `HSYNC = OR(H6,H4)` decode's active ranges should be
  enumerated by hand from `ICD6`/`ICD7`'s real preset values (5 and 9,
  confirmed net-derived, matching the current code) during phase 4a rather
  than assumed.
