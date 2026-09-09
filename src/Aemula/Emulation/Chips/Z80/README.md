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
