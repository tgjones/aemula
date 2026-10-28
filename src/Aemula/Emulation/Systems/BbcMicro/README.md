# BBC Micro

## Open source emulators

### Functional

* [jsbeeb](https://github.com/mattgodbolt/jsbeeb)
* [b2](https://github.com/tom-seddon/b2)
* [BeebEm](https://github.com/stardot/beebem-windows)
* [b-em](https://github.com/stardot/b-em)
* [MAME](https://github.com/mamedev/mame/blob/master/src/mame/machine/bbc.cpp)
* [beebjit](https://github.com/scarybeasts/beebjit)
  * Blog post: [Clocking a 6502 to 15GHz](https://scarybeastsecurity.blogspot.com/2020/04/clocking-6502-to-15ghz.html)

### In progress

* [bbc-em](https://github.com/gmbeard/bbc-em) in Rust

### FPGA

* [BeebFpga](https://github.com/hoglet67/BeebFpga)

## Manuals

* [A Hardware Guide for the BBC Microcomputer](http://archive.retro-kit.co.uk/bbc.nvg.org/doc/A%20Hardware%20Guide%20for%20the%20BBC%20Microcomputer/)
* [A Hardware Guide for the BBC Microcomputer (PDF)](http://archive.retro-kit.co.uk/bbcdocs.com/filebase/library/pubs/A%20Hardware%20Guide%20For%20The%20BBC%20Microcomputer.pdf)
* [BBC Microcomputer User Guide](http://central.kaserver5.org/Kasoft/Typeset/BBC/Contents.html)
* [BBC Microcomputer User Guide (PDF)](http://bbc.nvg.org/doc/BBCUserGuide-1.00.pdf)
* [Advanced User Guide for the BBC Microcomputer (PDF)](http://stardot.org.uk/mirrors/www.bbcdocs.com/filebase/essentials/BBC%20Microcomputer%20Advanced%20User%20Guide.pdf)
* [Service Manual for the BBC Microcomputer](http://www.computinghistory.org.uk/downloads/5285)
* [BBC Microcomputer Service Manual](http://chrisacorns.computinghistory.org.uk/docs/Acorn/Manuals/Acorn_BBCSMOct85_Sec1.pdf)

## Information

* [Original specification for the BBC Micro](http://www.bbcbasic.co.uk/bbcbasic/beebspec.html)
* [BBC Micro on an FPGA](https://www.mike-stirling.com/retro-fpga/bbc-micro-on-an-fpga/bbc-micro-on-an-fpga-design-detail/)
* [Hardware register tables](http://mdfs.net/Docs/Comp/BBC/Hardware/)
* [Memory map](http://mdfs.net/Docs/Comp/BBC/AllMem)

## Implementation details

* [Inside the BBC B](http://8bs.com/inbbc.htm)

### Address translation

* [Address translation page on BeebWiki](http://beebwiki.mdfs.net/Address_translation)
* [FPGA implementation of address translation](https://github.com/hoglet67/BeebFpga/blob/cde79b5913196e2a900a71c41bf3f2741de2fc14/src/common/bbc_micro_core.vhd#L1707)

## Master tick

One tick is one cycle of the 16MHz crystal (X1), so `CyclesPerSecond` is
16,000,000 and a 2MHz CPU cycle is eight ticks. 16MHz is the finest clock on the
board: the video ULA divides it to 8, 4 and 2MHz (the 1MHz bus clock, 1MHZE, is
IC34 halving 2MHz), and the synchroniser delays the CPU's clock by two 8MHz
edges, so nothing coarser can place its edges. Each tick:

* the ULA's divider and the reset 555 advance, and the CRTC's clock input
  follows the ULA;
* the clock glue (IC28-IC31, IC34, IC76, IC97, the NMI gate, the CPU clock gate)
  is evaluated net by net and repeated until nothing changes (normally two or
  three passes, at most 16);
* the CPU, VIAs and CRTC are not stepped every tick but on the edges of their
  own clocks (PHI2 for the CPU, 1MHZE for the VIAs, the character clock for
  the CRTC), so they cost nothing between edges.

The glue evaluation every tick is the part that does not scale. Replacing it
with event-driven evaluation, or with phase tables for the always-quiet
dividers, is deferred to the performance phase, and the benchmark will show
what it costs.

## Chips

### Video ULA

* [Video ULA](http://beebwiki.mdfs.net/Video_ULA)
* [Mike Stirling's FPGA BBC `vidproc.vhd`](https://github.com/mikestir/fpga-bbc/blob/master/vidproc.vhd)

### Serial ULA

* [hoglet67/SerialULA](https://github.com/hoglet67/SerialULA)

## Schematics

* [amb5l's searchable Issue 7 mainboard schematic](https://stardot.org.uk/forums/viewtopic.php?t=23329)
  (`bbc_micro_mainboard_iss7_20210909.pdf`), the netlist extraction source
* [TobyLobster's circuit diagrams](https://tobylobster.github.io/mos/mos/S-s25.html)
  (`CircuitDiagram.pdf`, `CircuitKeyboard.pdf`), a second source of truth

## Reference implementations

Aemula's BBC Micro emulation is tested against the excellent jsbeeb and b2 as 
reference implementations.

For jsbeeb, a harness exists at `tools/jsbeeb-harness`, built on its
[`MachineSession`](https://github.com/mattgodbolt/jsbeeb/blob/main/src/machine-session.js).

For b2, there's an HTTP API. The HTTP API doesn't expose as much information as
we can get from jsbeeb, so we'll use it as a manual tie-breaker. To use it, 
build b2 from source  (`make _unixr OS=osx`, then `ninja b2` in `build/r.osx`;
the `RelWithDebInfo` build includes the debugger and HTTP API). The
HTTP API listens on `localhost:48075` and is documented at the end of
[`doc/Debug-version.md`](https://github.com/tom-seddon/b2/blob/master/doc/Debug-version.md). Findings from running it:

* **Memory peek/poke works**: `GET /peek/b2/<begin>/+<len>` returns raw bytes
  (host address space, max `0xffff`), `POST /poke/b2/<addr>` stores the body.
  After `reset` the banner was readable from screen RAM at `&7C00`.
* Also available: `reset` (power-on, by config name), `paste`, `mount`, `run`,
  `load-disc`.
* **Not available over HTTP**: CPU registers, cycle counter, stop/run/step,
  breakpoints, PC trace. Peeks sample the machine while it is running, so
  there is no way to compare state at an exact cycle. Those features exist
  only in the GUI debugger (trace to file, manual stop).
* The configs saved in a fresh b2 profile are 1770-based (`B/Acorn 1770`
  etc.); an 8271 configuration has to be added by hand in the GUI before the
  phase 8 comparison.

## Netlist observations

Places where `bbc-netlist.txt` as extracted disagrees with how the board has to
work, and how `BbcMicroSystem` treats them. For the user verification passes.

* **Solder links.** The netlist cannot say which links are made. Assumed as on
  a stock Model B: S15/S16 and S17 made (FRED and JIM are 1MHz-bus pages),
  LK35-LK38 made with S21 joining pads 2 and 4 (sockets answer at
  &8000-&BFFF), S2 and the station-number links open (no Econet), S18/S19 open.
* **Paged ROM numbers.** IC20's second half decodes only the latch's low two
  bits (QA, QB), so each socket answers to four ROM numbers. BASIC is put in
  the socket that answers to 15, and a DFS goes in the socket for 14.
* **IC14's direction pin.** The netlist puts IC14.1 (DIR of the 74LS245 between
  the RAM data bus and the CPU's) on W/R, the inverse of R/W, with pins 2-9 (A)
  on BD and 11-18 (B) on D. Taken literally, a write would drive BD onto the
  CPU's bus. `BbcMicroSystem` passes data the way the board must work: RAM to
  CPU on a read, CPU to RAM on a write.
* **RAM is not contended.** The DRAM is accessed at 4MHz, the CPU taking one
  slot of every 2MHz cycle and the video ULA the other (see `StepRam`), so
  neither the 1MHz nor the 2MHz character-clock modes ever hold the CPU back;
  jsbeeb agrees. Only the 1MHz bus stretches the CPU.
* **Video fetch alignment.** The byte the ULA latches is the one fetched in the
  video slot before its edge, so with the 6845 clocked on that same edge it is
  the byte for the address the 6845 put out for the previous character. The
  ULA's `Disen` input is sampled one edge later; the alignment of the picture to
  the sync pulses is to be settled against jsbeeb in the screenshot comparison.
* **`_N352` (ULA pin 4).** IC38 exclusive-ORs it with MA6 to make RA6 in Mode 7.
  The ULA's teletext timing output is not modelled and is taken as low, which
  gives the MA-only addresses jsbeeb uses.
