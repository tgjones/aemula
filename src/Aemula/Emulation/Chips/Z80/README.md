# Zilog Z80

Target: the **NMOS Zilog Z80** (the die the ZEXALL / z80test suites are written
against). CMOS and clone (NEC, etc.) differences in `SCF`/`CCF` flag behaviour
and `OUT (C),0` are out of scope.

## Manuals

* **Zilog Z80 CPU User Manual** (UM0080) - pin timing diagrams; the source of
  the half-T-state edge map baked into `Z80Chip.cs`.
* **"The Undocumented Z80 Documented"**, Sean Young - X/Y flags, MEMPTR/WZ,
  block-op and `IN`/`OUT` flag formulas, `DD CB` behaviour. Authoritative for
  everything ZEXALL / z80test check.
* **z80.info/decoding.htm** - the octal `x`/`y`/`z`/`p`/`q` opcode-decode scheme
  the microcode is structured around.

## Other implementations

* **floooh/chips `z80.h`** (Andre Weissflog, MIT) - a tick-accurate, pin-level
  Z80 in C; the closest analogue to this one. Read for per-tick pin sequencing.
* **superzazu/z80** (MIT) - compact instruction-level core that passes ZEXALL;
  good for cross-checking opcode semantics quickly.
* **FUSE `z80.c` / `z80_ops.c`** (GPL - read for understanding, do **not** copy) -
  semantics, and the origin of the `tests.in` / `tests.expected` bus-timing data.
* This repo's own `Intel8080/Intel8080Chip.cs` - the pin-style API and
  staged-cycle state-machine pattern (no code is shared).

## Test data & suites

The chip's tests live in `src/Aemula.Tests/Emulation/Chips/Z80/`.

* **`Assets/tests.in` / `Assets/tests.expected`** - the per-instruction
  bus-timing vectors from the **fuse-emulator** project
  (`z80/tests/` in its source tree). Plain text, redistributable; no FUSE code
  is used. Drive `Z80ChipBusTimingTests` (address + byte + T-state stamp of
  every `MR`/`MW`/`PR`/`PW`; the `MC`/`PC` ULA-contention rows are parsed but
  not asserted - that is board-level behaviour, not a CPU action).
* **`zexdoc.com` / `zexall.com`** - Frank Cringle's Z80 instruction exerciser
  (documented- and all-flags variants). Run to a full pass, with an exact
  total-T-state-count regression ratchet pinned per ROM.
* **`z80doc.tap` / `z80docflags.tap`** - Patrik Rak's `z80test` suite (MIT),
  documented-flags ROMs. Run to a full pass, also T-state-ratcheted.
* **`z80flags.tap` / `z80full.tap` / `z80ccf.tap` / `z80memptr.tap`** - the
  remaining `z80test` ROMs. Committed but `[Skip]`ped: each fails only the two
  self-modifying block-repeat subtests 102/103 (`INIR->NOP'` / `INDR->NOP'`),
  whose expected undocumented flags need a "block-I/O interrupted" formula that
  in turn regresses a FUSE `INIR`-repeat-tail bus-timing vector - the two are
  mutually exclusive on this core, so FUSE is kept green and these stay skipped.
