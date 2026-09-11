# Sinclair ZX80

## Information

* [ZX80 on Wikipedia](https://en.wikipedia.org/wiki/Sinclair_ZX80)
* [ZX80 character set](https://en.wikipedia.org/wiki/ZX80_character_set)
* [Tynemouth Software: How the ZX80 works](http://blog.tynemouthsoftware.co.uk/2019/10/how-the-zx80-works.html) - the split data bus, the NOP generator, and how the Z80's own refresh cycle doubles as video addressing
* [Tynemouth Software: ZX80 USA](http://blog.tynemouthsoftware.co.uk/2022/10/zx80-usa.html) - the US/NTSC board variant this emulator targets first: the D11 strap diode and the 50Hz/60Hz ROM branch it selects
* [Tynemouth Software: How the ZX81 Generates Video](http://blog.tynemouthsoftware.co.uk/2023/10/how-the-zx81-generates-video.html) - a second independent writeup of the same class of trick, useful as a cross-check

## Schematic

[TankedThomas/ZX80](https://github.com/TankedThomas/ZX80) is a redrawn,
corrected KiCad reproduction of the original board:

* [zx80_schematic_kicad.pdf](https://github.com/TankedThomas/ZX80/blob/master/Docs/zx80_schematic_kicad.pdf) - the schematic itself
* [ZX80_Board.kicad_sch](https://github.com/TankedThomas/ZX80/blob/master/ZX80_Board.kicad_sch) - the plain-text KiCad source the PDF is generated from
* [ZX80.kicad_sym](https://github.com/TankedThomas/ZX80/blob/master/ZX80.kicad_sym) - symbol library
* [zx80_parts_list.csv](https://github.com/TankedThomas/ZX80/blob/master/Docs/zx80_parts_list.csv) - full BOM with reference designators, including the PAL-vs-NTSC fitted/not-fitted differences

## ROMs

Not yet sourced - see [`docs/zx80-plan.md`](../../../../../docs/zx80-plan.md)
for sourcing notes while this is in progress.

## Other implementations

* [MAME](https://github.com/mamedev/mame/blob/master/src/mame/sinclair/zx.cpp)
* [EightyOne](https://github.com/charlierobson/EightyOne) - Sinclair ZX80/ZX81/Spectrum emulator
* [sz81](https://github.com/ikjordan/sz81_2_1_8) - a ZX81 emulator descended from EightyOne, with ZX80 support
