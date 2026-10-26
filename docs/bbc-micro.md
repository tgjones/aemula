# BBC Micro Model B — Implementation Plan

## Goal

Add a BBC Micro Model B (Issue 7 board, OS 1.20, BASIC 2, 32K RAM, 8271 disc
controller) to Aemula, running in real time. It's the most complex system in
the repo: two 6522 VIAs, a 6845 CRTC, two custom Ferranti ULAs, a teletext
generator, a sound chip, an ACIA, an FDC, and a clock scheme in which the CPU
runs at 1 or 2 MHz in lock-step with video fetches from shared DRAM.

Follows the existing conventions (see `AppleII` / `ZX80`):

- `EmulatedSystem` subclass `BbcMicroSystem` in
  `Emulation/Systems/BbcMicro/` (partial classes per concern, like
  `ZX80System.*.cs`), replacing the commented-out `BbcMicroModelB.cs` stub.
- New chips in `Emulation/Chips/<Name>/` when they have companion files,
  otherwise flat; generic 74xx parts flat as `TtlNNNNNChip.cs`.
- Debugger in `Systems/BbcMicro/Debugging/`, reusing `Mos6502Disassembler`;
  UI side via `DebuggerUIFactory` (see the UI decoupling note) and registered
  in the `Systems` dictionary / `EmulatedSystems`.
- Tests per chip in `Aemula.Tests/Emulation/Chips/`, per system in
  `Aemula.Tests/Emulation/Systems/BbcMicro/`.
- Code and test comments state hardware facts, never "Phase N" or "per the plan".

## Target configuration

**Model B, Issue 7, OS 1.20, BASIC 2, 32K RAM, 8271 FDC + DFS, PAL.**

In scope for the baseline: 6502A, RAM, ROM sockets with paged-ROM select, both
VIAs, 6845, Video ULA, SAA5050 (Mode 7), keyboard, SN76489, cassette (Serial ULA
+ 6850), user/printer ports as far as the OS touches them, 8271 FDC with
`.ssd`/`.dsd` images.

Out of scope (listed in "Later"): µPD7002 ADC (analog joysticks), TMS5220
speech, Econet, Tube, 1 MHz bus devices, B+/Master variants.

Already in the repo: `Roms/os.rom` (CRC32 `3c14fc70`, matches MAME's `os12`,
i.e. OS 1.20) and `Roms/BASIC.rom` (CRC32 `79434781`, BASIC 2). A DFS ROM is
**not** present; see Open questions.

## Fidelity approach

Agreed split, in the same spirit as the Apple II / ZX80 work:

- **Netlist-driven (pin-level chips wired net-by-net):** the 6502, VIAs, 6845,
  and the 74xx glue that defines timing and bus behaviour — clock
  stretching/synchronisation to the 1 MHz bus, ROM/RAM/IO address decode,
  paged-ROM select, the DRAM address multiplexers and RAS/CAS/bus-arbitration
  logic, interrupt gating, reset, the keyboard/latch logic.
- **Behavioural (modelled from documented behaviour, not wires):**
  - *Video ULA* and *Serial ULA* (no die-level netlist exists; port from
    Mike Stirling's VHDL / `hoglet67/SerialULA` and the beebwiki description).
  - *SAA5050* teletext generator (cell geometry and control codes from MAME's
    `saa5050` and the datasheet; no netlist exists).
  - *Analog sections* — op-amp/comparator cassette front-end, audio filter and
    amp, RGB/UHF output stages — reduced to their logical/electrical effect.
  - *8271 FDC* (command-level model, like jsbeeb/b-em).
- **DRAM is an array plus contention.** The 16 HM4816s are a flat 32 KB
  `byte[]`. The *timing* of who owns RAM on each 250 ns slot (CPU vs. video
  fetch) comes from the netlist-driven arbitration logic, so contention and
  stretched cycles are cycle-exact. RAS/CAS-level electrical modelling and
  refresh decay are deliberately omitted (refresh is a side effect of the 6845
  scan and not observable). If a test ever shows the abstraction is wrong, an
  electrical model can be slotted in behind the same interface.
- ROMs are plain `byte[]` behind the decode logic, as elsewhere.

## Schematic source and netlist extraction

Source: `amb5l`'s
[searchable Issue 7 mainboard schematic](https://stardot.org.uk/forums/viewtopic.php?t=23329)
(`bbc_micro_mainboard_iss7_20210909.pdf`, one A3 sheet exported from OrCAD).
Verified in research: it has a real text layer (refs, pin numbers, net labels)
and vector wires — about 8.5k wire segments in one stroke colour, about 1.1k
bus segments in another, part outlines/pins in two more. The same approach
produced `Systems/ZX80/docs/zx80-netlist.txt`.

Known errors in this schematic (apply as corrections, cross-check against the
service manual main-PCB circuit diagram, appendix 9.5):

- IC93A pins 4/5 and IC93B pins 10/11 labels reversed; the wire from IC93A.5
  should go to .4, and the junction goes to IC89.6 (TxD) and IC93B.11, not
  IC89.2 (RTS) / IC91A.1,2. (RS423 area.)
- IC27 pins 9 and 10 transposed.
- Keyboard circuit has a spurious extra wire (reported by IanS; confirmed by
  TobyLobster, whose
  [vector diagrams](https://tobylobster.github.io/mos/mos/S-s25.html) —
  `CircuitDiagram.pdf` / `CircuitKeyboard.pdf` — are a second source of truth).

Extracted netlist format and conventions follow the ZX80 netlist (COMPONENTS /
NETS, leading `/` for active-low, `_Nnnn` for unnamed nets, a header recording
every correction). The extractor lives in `tools/` (not in the Aemula
solution) and is rerunnable; hand corrections are layered on top as a
documented patch list rather than editing the generated file.

The 8271 FDC, µPD7002, TMS5220/TMS6100 and Econet parts are on the sheet, so
the extractor covers them even though several are later phases.

## Reference emulators

Primary, scriptable: **jsbeeb** headless via `MachineSession`
(`src/machine-session.js`): `runFor(cycles)`, `readMemory`/`writeMemory`,
`registers()`, `onExecute(pc)`, breakpoints, `type()`, disc/tape loading,
`screenshot()`. Used for differential tests: same program, N cycles, compare
registers/RAM/PC trace/frame output.

Secondary: **b2** (Tom Seddon) — believed to be among the most cycle-accurate
(unverified; ranking is from memory). It's a C++ GUI app; its README says the
*debug build* has "a simple HTTP API for remote control" and command-line
startup options. Whether that includes memory peek/poke is **not yet known**
(first task in phase 0). Use it as a tie-breaker when Aemula and jsbeeb
disagree; manual use is acceptable.

Tertiary: MAME's `bbc.cpp` (less cycle-exact on video) and **beebjit** as sanity
checks. Neither is silicon; agreement is evidence, not proof.

## Chip inventory

Already present in `Emulation/Chips/` and reusable as-is: `Mos6502Chip`,
`Ttl7400`, `7402`, `7404`, `7410`, `7420`, `7432`, `7474`, `7486`, `74123`,
`74138`, `74139`, `74259`, `74283`.

**Needed, generic 74xx/CMOS (flat files, with tests):**
`Ttl7430`, `Ttl7438` (open-collector NAND), `Ttl7451`, `Ttl74132`
(Schmitt NAND), `Ttl74163`, `Ttl74244`, `Ttl74245`, `Ttl74273`, `Ttl74393`,
`Cd4013`, `Cd4020`. 74S00/74S04 reuse `Ttl7400/7404` (the S-family timing
difference is irrelevant to a zero-delay model, but note any place the
schematic relies on a propagation delay). The sheet's text layer may list
further types (e.g. the DRAM address multiplexers); the extraction phase
produces the definitive list and this section is updated then.

**Needed, system-specific:**

| Chip | Notes |
|---|---|
| `Mos6522Chip` | existing file is a register-only stub: needs timers 1/2, shift register, handshake, IRQ, pin-level ports. Two instances (System VIA, User VIA). |
| `Motorola6845Chip` | existing file throws `NotImplementedException`. Hitachi HD6845SP variant (the one fitted); R0–R17, MA/RA, DISPEN/HS/VS, cursor, light pen, update-address. |
| `VideoUla` | behavioural; control register, palette, pixel clock rates, cursor. |
| `Saa5050Chip` | behavioural teletext; character ROM data (see ROMs). |
| `Sn76489Chip` | 3 tone + noise; audio output through `SampleBuffer` as other systems do. |
| `SerialUla` | behavioural; cassette and RS423 baud/mode glue. |
| `Mos6850Chip` | ACIA. |
| `I8271Chip` | FDC, command-level. |

## ROMs

Present: OS 1.20, BASIC 2. Need to source and checksum-verify: SAA5050
character ROM data (if not derivable from the datasheet glyph set), DFS (e.g.
Acorn DFS 0.90 or 1.20), and a sideways-ROM-bank layout (OS in the OS socket;
BASIC in the highest-priority paged slot; DFS below it). Keep a
`Roms/README.txt` disclaimer as for the existing ROMs.

## Phased plan

Each phase ends compiling and with targeted tests green (never the full suite
— it takes ~40 minutes; run per-class filters). Per AGENTS.md: a multi-step
run commits between steps; a single step is left uncommitted for review.

Note to agents - when you complete a step, just tick the step, no need to add
a whole bunch of text here in the plan saying what you did. That's discoverable
from git history.

### Phase 0 — Reference harness and groundwork

- [x] Stand up a jsbeeb headless harness (Node script outside the Aemula
      solution, e.g. `tools/`) that runs a model-B session for N cycles and
      dumps registers, selected RAM, PC trace, and a frame PNG as JSON/files
      that Aemula tests can compare to.
- [x] Build b2's debug version locally; read `doc/Debug-version.md` and decide
      whether its HTTP API is usable for memory/register inspection. Record
      the finding in `Systems/BbcMicro/README.md`.
- [x] Confirm the ROM CRCs above in a test; collect DFS and SAA5050 sources.
- [x] Update `Systems/BbcMicro/README.md` with the links found in research
      (schematic thread, Stirling's vidproc, SerialULA, jsbeeb
      `MachineSession`, b2 debug docs).

### Phase 1 — Netlist extraction

- [x] Write the extractor: parse the PDF's vector geometry and text layer
      (wires, junction dots, pin ends, labels, bus rippers) into the ZX80
      netlist format. Report suspicious nodes (dangling or passive-only,
      unlabelled nets joining two outputs, both-ends-same-bus buffers) as
      diagnostics — these were the tell-tales last time.
- [ ] Apply the known corrections above; emit
      `Systems/BbcMicro/docs/bbc-netlist.txt`.
- [ ] Sanity checks: every IC has all its power/ground pins; every gate input
      is driven; bus-by-label connectivity (A0–A15, D0–D7, address/data bus
      labels) resolves.
- [ ] **User verification passes** over the areas most likely to be wrong
      (RS423/Econet and the keyboard are the expected trouble spots). The
      first pass is mine; wherever I get stuck, the user traces that area by
      hand. Record each pass in the netlist header as the ZX80 did.
- [ ] Update the chip inventory above from the real component list.

### Phase 2 — Missing generic chips

- [ ] Implement the generic parts listed above, pin-level, with
      truth-table/behaviour tests per class.

### Phase 3 — 6522 VIA and 6845 CRTC

Both are stubs today and everything else depends on them; they get their own
phase with thorough tests.

- [ ] `Mos6522Chip`: pin-level; T1 (one-shot/free-run, PB7), T2 (one-shot,
      pulse count), SR modes, CA1/CA2/CB1/CB2 handshake, IFR/IER/IRQ, the
      read-clears-flag and write-clears-flag rules and their off-by-one cycle
      edge cases. Tests from the datasheet behaviour plus a jsbeeb
      differential on timer/IRQ timing.
- [ ] `Motorola6845Chip`: HD6845SP, all registers and masks, character/row/
      frame counters, vertical adjust, interlace modes, cursor blink/modes,
      DISPEN/HS/VS widths, light pen, R14/R15 and R16/R17. Differential test
      versus jsbeeb's CRTC output for the BBC's standard mode register sets
      (Mode 0, 4, 7, and a nonstandard one).

### Phase 4 — CPU, memory, decode, clocks (no video yet)

- [ ] Create `BbcMicroSystem` wiring from the netlist: 6502A, 32K RAM array,
      OS ROM, paged ROM slots, address decode, SHEILA register space, both VIAs
      present, ROM-select latch, reset circuit, the 16 MHz oscillator and
      divider chain.
- [ ] The 1 MHz bus synchronisation: IO accesses stretch the CPU clock to
      1 MHz phase; model it from the netlist (do not special-case).
- [ ] Decide and document the master tick (likely 16 MHz, with chips
      evaluated only when their inputs or clock edge change — see
      Performance).
- [ ] Interim video-less verification: run OS 1.20 and compare the PC trace
      and cycle count to jsbeeb up to the first keyboard poll; checkpoint RAM
      and VIA register states at fixed cycle counts. The 6845 and ULA stubs
      here are inert so the CPU is not contended yet — expected divergence
      from jsbeeb at the first video fetch is noted and removed in phase 5.
- [ ] Add `BbcMicroBenchmark` to `Aemula.Benchmarks` now (ns/cycle baseline),
      as was done for the ZX80, so every later phase sees the cost it adds.

### Phase 5 — Video, monochrome composite

- [ ] `VideoUla` behavioural: control register (clock rate, cursor, teletext
      select, flash, palette) and palette register, pixel shift per mode,
      clock outputs, RAM-fetch addressing handshake with the 6845 (`MA`/`RA`
      → screen-address wrap and the Mode 7 / non-Mode 7 wraparound
      latches). Source: Stirling's `vidproc_orig.vhd` and beebwiki; test
      mode-by-mode against jsbeeb.
- [ ] RAM contention: the DRAM address multiplexing and the CPU/video access
      alternation, taken from the netlist; at 2 MHz CPU, and with 1 MHz CPU
      for 40-column (clock-rate bit) modes. Verify cycle-by-cycle against
      jsbeeb on a RAM-heavy loop in each mode.
- [ ] `Saa5050Chip` (Mode 7): control codes, held graphics, double height,
      flash, separated graphics; take the glyph/row timing from the
      datasheet/MAME and compare screenshots of known Mode 7 screens.
- [ ] Monochrome composite out: sum R/G/B to a luma level, combine with the
      6845's HS/VS through the on-board sync network, and push samples into
      `Television.Decode` as the other systems do (television signal
      propagation note). Colour is ignored at this stage.
- [ ] Verification: `Aemula.Console` headless screenshot of the BASIC banner
      and of a Mode 7 screen; frame-level comparison against jsbeeb
      screenshots (mono-converted). Check line/frame timing is exactly
      PAL 312 lines / 50 Hz field and that AFC locks.

### Phase 6 — Keyboard, input, sound

- [ ] Keyboard matrix via the System VIA and scanning latch logic from the
      netlist; map host `KeyEvent`s via `Key` to matrix positions; BREAK key
      to reset. Typing `PRINT "HI"` headlessly via `Aemula.Console --input`
      is the end-to-end test.
- [ ] `Sn76489Chip` and audio out (volume table, tone/noise LFSR, clock/32).
- [ ] Printer/user-port/ADC-connector pins left idle but wired so the OS's
      probing behaves; joystick fire buttons if cheap.
- [ ] Register the system, console controls (RESET/BREAK, shift-lock LED,
      caps lock LED, disc LED), and the debugger UI.

### Phase 7 — Cassette

- [ ] `SerialUla` behavioural + `Mos6850Chip`; motor relay; cassette
      input/output paths (analog front-end reduced to a tone/level
      discriminator). Reuse the tape-deck transport from the Apple I ACI and
      ZX80 tape work as far as it applies; image formats UEF and WAV.
- [ ] Verify: load a BASIC program from a UEF and from a WAV; compare the CPU
      trace with jsbeeb across a short load.

### Phase 8 — 8271 disc controller

- [ ] `I8271Chip`: command/parameter/result protocol, the seek/read/write/
      format sector operations, NMI-driven data transfers, drive selection,
      and the head-load/step timing the OS relies on. Model on b-em / jsbeeb
      / MAME rather than a datasheet alone; cross-check timing in b2 if its
      API allows.
- [ ] Media: `.ssd` / `.dsd` via the `IMediaBayHost` / `MediaBay` mechanism.
- [ ] DFS ROM in the paged slots; boot a disc image with `*CAT`, `*RUN`,
      `CHAIN"..."`, and a real game or two (spot-check disc-protection-free
      titles).
- [ ] Compare disc-activity timing and results against jsbeeb.

### Phase 9 — RGB input path for Television

Colour on the Model B is RGB-only. This phase generalises `Television` so the
machine can feed it RGB + composite-sync instead of (or alongside) the
composite sample byte stream. Design notes to settle first:

- [ ] Where the split sits: the decode runs on a hidden background thread and
      every `Television` read is an implicit barrier; the new input must
      share that model and not add per-tick reads.
- [ ] Sync handling: RGB sources have no colour burst; the existing AFC/
      sync-separator and the colour killer only apply to the composite path.
      Re-use the horizontal/vertical sync recovery on the CSYNC channel.
- [ ] API: probably a `TelevisionInput` mode with either a packed
      `RGB+CSYNC` sample (8-colour, 1 bit each) at the same sample rate, or a
      pre-timed pixel stream; pick on the performance data from phase 4.
- [ ] Verify against existing systems — composite results of Apple II, Atari
      2600, NES, ZX80 must be unchanged (their test classes plus a screenshot
      diff).
- [ ] Wire the BBC video output to the RGB path with the ULA's flash and
      palette; compare coloured Mode 0/1/2/5 and teletext colours against
      jsbeeb.

### Phase 10 — Performance

Left until last by design, but the benchmark has been running since phase 4,
so there is data. Expected issues, to be confirmed by profiling
(`dotnet-trace`, `Aemula.Benchmarks`; Release builds only):

- A 16 MHz master tick with netlist-driven glue evaluated every tick is the
  likely killer. Options, in order of preference: event-driven evaluation
  (only recompute chips whose inputs changed), collapse always-quiet divider
  chains into precomputed phase tables, run the ULA/6845 at their natural
  rate (1/2 MHz) rather than the master tick, devirtualise the hot chip
  calls, and avoid reading `Television` per tick.
- Target: real time (50 fps PAL) on the user's Mac with headroom; record
  ns/cycle versus the other systems so regressions are visible.
- The `Array.Copy`/memmove and Debug-build traps found for earlier systems
  are known; check for them first.

## Later / not in this plan

µPD7002 ADC and analog joysticks, TMS5220 speech (+ TMS6100), Econet, Tube
(second processors), 1 MHz bus peripherals, alternative OS/BASIC versions,
Model B+, Master 128 (a KiCad schematic exists:
[simoninns/Acorn-Master-AIV](https://github.com/simoninns/Acorn-Master-AIV)),
RS423 as a usable port.

## Open questions and risks

- **DFS ROM source.** Which DFS version to ship/test with, and where it comes
  from (user to provide, or from jsbeeb's ROM set as with the OS?).
- **Video ULA exactness.** No die-level data exists; edge cases (mid-frame
  palette/control writes, cursor, 2/4/8 MHz pixel clock transitions) are
  based on observed behaviour in other emulators and FPGA ports, so are
  likely a source of residual diffs versus real hardware.
- **Cycle-exactness of the contention model** depends on correctly extracting
  the clock/arbitration part of the netlist; this is the highest-value part
  of the verification passes in phase 1.
- **SAA5050 glyph data.** The character-generation ROM contents may need to
  be sourced or reconstructed; check before phase 5.
- **Netlist extraction quality** on a dense single-sheet drawing: the ZX80
  needed three user-verified correction passes; expect at least as many.
- **Performance may not reach real time** with the planned architecture; the
  early benchmark exists to find out before phase 10.
- **b2 scriptability** is unverified (phase 0).
- **Test material.** Beyond the OS boot and BASIC, a set of cycle-timing test
  programs (VIA timers, 6845 timing, interlace, contention) needs to be found
  or written; jsbeeb provides the oracle.
