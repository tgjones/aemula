# Apple I

## Information

* [Apple I on Wikipedia](https://en.wikipedia.org/wiki/Apple_I)
* [Apple-1 Schematic (1976)](https://archive.org/details/Apple1Schematic1976) - Wozniak's original drawing
* [Apple-1 Registry - software and documents](https://www.apple1registry.com/en/soft.html) - Operation Manual, Cassette Interface Manual, BASIC manual
* [Inside the Apple-1's shift register memory](https://www.righto.com/2022/04/inside-apple-1s-shift-register-memory.html) by Ken Shirriff

## Netlist

[`docs/apple-i-netlist.txt`](docs/apple-i-netlist.txt) is a machine-readable
component/net dump extracted from the "a1 circuit.pdf" P-CAD redraw of the
schematic (by MIKE_A, 2015). It covers sheet 1 (terminal) and sheet 2
(processor); the power supply is omitted. The header comments describe the
line format and the assumptions made about pin numbering and diode/transistor
polarity.

The source PDF is [`a1 circuit.pdf`](http://retro.hansotten.nl/uploads/apple1/a1%20circuit.pdf).

## ROMs

The only ROM on the board is the 256-byte Monitor (WozMon), inlined as data in
[`Roms/WozMonitor.cs`](Roms/WozMonitor.cs) rather than shipped as a binary
asset. On real hardware it is two 256x4 bipolar PROMs (ICA1/ICA2) forming one
image mirrored across `$FF00`-`$FFFF`. The bytes are cross-checked against
[jefftranter/6502](https://github.com/jefftranter/6502/blob/master/asm/wozmon/wozmon.s)'s
reassembled `wozmon.s`, itself transcribed from Wozniak's original listing.

The character generator is a Signetics 2513, the literal same physical part the
Apple II sockets, so it lives in `Emulation/Chips/Roms` as a shared chip class.

## Other implementations

* [pom1](https://sourceforge.net/projects/pom1/)
* [MAME](https://github.com/mamedev/mame/blob/master/src/mame/apple/apple1.cpp)
* [OpenEmulator](https://github.com/OpenEmulatorProject/OpenEmulator)
