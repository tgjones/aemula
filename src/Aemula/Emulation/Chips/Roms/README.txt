ROMs owned by a chip class rather than by any one system, because the real
part shows up unmodified on more than one board.

Signetics2513.rom - Signetics 2513 "64x8x5 Character Generator" mask ROM.
Used, unmodified, in both the Apple I (ICD2) and the early Apple II/II+
(same PN 341-0036) - both boards socket the literal same physical chip
alongside a discrete video inverter, rather than the character ROM itself
encoding inverse/flash video (that came later, on a different part).

512 bytes (64 characters x 8 rows), the chip's whole addressable content
(Address1-3 select the row, Address4-9 the character - see
Signetics2513Chip.cs). Trimmed from the first 512 bytes of the AppleWin
project's Apple2_Video.rom dump (see ../../Systems/AppleII/Roms/README.txt
for that file's own provenance) - the remaining 1536 bytes of that dump are
a later Apple II ROM revision's baked-in inverse/flash variants, which is a
different, larger part this codebase doesn't model.

Not GPL, still copyrighted - happy to remove if asked, see the AppleII
Roms README.
