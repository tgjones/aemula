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
