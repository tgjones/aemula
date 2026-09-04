# Apple I — Implementation Plan

## Goal

Add a pin-level Apple I emulator to Aemula, following the exact same shape as
the existing Apple II support:

- `EmulatedSystem` subclass in `Emulation/Systems/AppleI/AppleISystem.cs`
  (shape mirrors `AppleIISystem.cs`), split into partial-class files by
  subsystem the same way (`.CompositeVideo.cs`, `.Video.cs`, `.Keyboard.cs`,
  etc.).
- New chip classes under `Emulation/Chips/`: a generic, reusable 74xx part
  goes flat (`Ttl7410Chip.cs` etc.) next to the ones Apple II/Atari 2600
  already added; a part complex enough to want companion files (the PIA)
  gets its own folder, following `Emulation/Chips/Mos6532/`'s precedent.
- ROM in `Emulation/Systems/AppleI/Roms/` — **not sourced yet, see ROM
  section below.**
- Debugger in `Emulation/Systems/AppleI/Debugging/AppleIDebugger.cs`, reusing
  `Mos6502Disassembler` exactly like `AppleIIDebugger`.
- Registered as `"applei"` in `Aemula.UI/SystemCatalog.cs`, `Aemula.Console/
  SystemRegistry.cs`, and given an `AppleIBenchmark` in `Aemula.Benchmarks/
  Benchmarks/SystemBenchmark.cs` + a `SystemSpecs` entry, same as every other
  system.
- Tests in `Aemula.Tests/Emulation/Systems/AppleI/`, one file per concern,
  mirroring the `AppleII/` test folder.

`Television`/`NtscYiqDecoder` are a working NTSC decode pipeline already
(Apple II, Atari 2600, and NES all feed it). Apple I plugs into that same
pipeline; no new `Television` work is anticipated (see "Composite video"
below).

## The schematic

This plan is now built directly off
[**http://retro.hansotten.nl/uploads/apple1/a1%20circuit.pdf**](http://retro.hansotten.nl/uploads/apple1/a1%20circuit.pdf)
— a genuine vector P-CAD export (not a scan), 3 sheets: Terminal Section,
Processor Section, Power Supply. I rendered each sheet at 300dpi and read it
tile-by-tile at full resolution, so the chip inventory and net names below
are transcribed from the real schematic, not inferred from prose
descriptions. This is a materially better source than what the previous
version of this plan had to work with — treat the inventory below as
confirmed, not a starting point to re-derive during implementation.

The DigiBarn 1976 original
([archive.org](https://archive.org/details/Apple1Schematic1976)) is worth
keeping as a secondary cross-check in case this board revision differs from
Woz's original in some small way, but the hansotten PDF is the primary
reference from here on.

## Why this is a smaller project than Apple II

Confirmed from the schematic, not just "probably simpler":

- **No color.** Sheet 1 has no color-subcarrier signal anywhere on it — the
  composite output (Q5's transistor stage, fed by `VID` and sync only) is
  luma+sync, full stop. No color-burst gate, no chroma summing network.
  `NtscColorBurstPll` already degrades gracefully with no burst ever
  detected, so the existing `Television` pipeline should render this as a
  clean grayscale picture with no decoder changes.
- **No onboard keyboard encoder chip.** Confirmed on the schematic: the `B4`
  keyboard connector wires straight into the PIA (`ICA4`, `MC6820`) — `PA0`-
  `PA7` for data, `CA1` for the keyboard strobe. There is no encoder chip
  between the keyboard and the PIA at all (contrast the Apple II's
  `AY-5-3600`). SDL key events map straight to an ASCII byte + strobe pulse
  into the PIA.
- **No graphics modes, no soft switches.** One display mode, 40×24 text,
  always on. No lo-res/hi-res, no mode switches, no annunciators, no game
  paddles, no speaker.
- **No BASIC in ROM.** The 256-byte ROM (two 256×4 PROMs, `ICA1`/`ICA2`) is
  the Monitor (WozMon) only. Integer BASIC had to be hand-typed or loaded
  from cassette. The Monitor-only target (phase 4 below) is already a
  complete, useful emulator with no cassette support required.
- **No discrete CPU clock-shaping circuit, for the 6502 build.** Schematic
  note 7: the board can take either a 6502 or a 6800, and an entire cluster
  — `ICC1` (a 7404 used as a delay chain), four discrete transistors
  (`Q1`-`Q4`), and their surrounding R/C network — exists **only** for a
  6800 substitution ("if a 6800 is substituted... install all components
  shown [in the dotted box]... unit as supplied [with a 6502] has omitted
  all components within the dotted box"). The 6502 needs one single-phase
  clock input; the two-phase φ1/φ2 shaping that circuit exists to produce is
  a 6800 requirement, not a 6502 one. **This whole cluster is out of scope —
  it's not part of the board we're building.**

One place Apple I's design is *more* unusual than Apple II's, not simpler:
the 40×24 character grid isn't stored in RAM at all. See the next section.

## Target configuration

**Base Apple I board, 6502 (not 6800), NTSC, 8K RAM (both onboard DRAM
banks populated), Monitor ROM only, no DMA, no cassette card, no 64K
expansion card.**

8K (both `MK4096` banks, confirmed as the schematic's shipped default — see
chip inventory) is the practical choice: it's plenty to hand-type and run a
real BASIC session once phase 4 is done.

Out of scope for the initial build: the optional cassette interface card
(the 480nsec half of `ICB3`'s 74123 exists for it; not populated on the base
board's active path otherwise), the 64K RAM expansion card, and DMA (Note 13
on the schematic: DMA support means swapping four `74157`s for tri-state
`74S257`s at positions B5-B8 and is explicitly an "if required" option, not
part of the base board).

## Fidelity approach

Same spirit as the Apple II plan: **full gate-level fidelity**, with one
exception for bulk storage:

- **DRAM stays a plain `byte[]`.** The 16 `MK4096` (4096×1 DRAM) chips
  behind the address-decode/mux logic are a `byte[0x2000]` (two 4K×8 banks),
  exactly like `AppleIISystem._ram` — only the addressing logic (the
  `74157`/`74S257` row/column multiplexers, `RAS`/`CAS` generation) is
  simulated. DRAM refresh is a side effect of the video scan, same reasoning
  as Apple II.

**The character "memory" is modeled as real shift-register chips, not a
byte-array approximation** — per your direction, and because the schematic
makes clear this is the one place doing so is actually the more natural
model, not just the more faithful one. There is no address bus into this
storage at all: the CPU and the video scan both access character positions
only by *when* they intercept a continuously-recirculating serial bit
stream. Concretely, from the schematic:

- **7× `2504`** (`ICD4A`, `ICD4B`, `ICD5A`, `ICD5B`, `ICC11B`, `ICD14A`,
  `ICD14B`) — single-bit-wide dynamic PMOS recirculating shift registers.
  Six are almost certainly the six character-code bit-planes (`2513`'s
  address inputs `A4`-`A9` are 6 bits, i.e. 64 characters); the 7th
  (`ICC11B`) carries some additional bit through the same delay line whose
  exact role isn't pinned down yet — confirm net-by-net in phase 4 rather
  than assume. Each is modeled as a real `Signetics2504Chip`: an `IN` pin, a
  two-phase (`PHI1`/`PHI2`) dynamic clock that actually shifts the register
  by one position per clock cycle, and an `OUT` pin — i.e. a genuine
  circular shift, not a `byte[]` with a modulo index standing in for one.
- **1× `2519`** (`ICC3`) — a 40-position × 6-bit-wide recirculating shift
  register (`IN1`-`IN6`/`OUT1`-`OUT6`, one `RC`/`CLK` per Note 1) that
  buffers one active row of 40 character codes, read out 8 times (once per
  character scanline) before advancing. Same treatment: a real
  `Signetics2519Chip`, not a lookup table.
- **The write mechanism is real, not simulated as "poke a byte in":** two
  `74157`s (`ICC4`, `ICC14`) select, per shift-register bit position and per
  clock, between the recirculating bit (`I0`, wired back from each 2504's
  own output) and a new bit from the PIA's display-data port (`I1`, the
  `RD1`-`RD7` nets sourced from the PIA's `PB0`-`PB6`), driven by a `WRITE`
  select line. That `WRITE` line is itself the output of comparator logic
  (`ICC5`-`ICC9` gates + the `ICC7`/`ICC13` registers) that detects *the
  exact clock cycle* the recirculating stream reaches the stored cursor
  column — i.e. "writing a character" on real hardware means momentarily
  breaking the recirculation loop at the right moment, not an indexed
  store. This is genuinely the trickiest part of the whole system and the
  one place worth budgeting real implementation time (phase 4).
- **The two-phase MOS clock for all of the above comes from one chip**: a
  `DS0025` (`ICC11A`), a dual high-current MOS clock driver, level-shifting
  a TTL-derived timing signal up to the swing the PMOS 2504/2519 parts need.
  Modeled as a small `Ds0025Chip` — two independent driver channels, no
  internal state beyond pass-through-with-timing, but a distinct real part
  worth its own class for the same reason `Ne555Chip` gets one.
- **`2513`** (`ICD2`, the character generator) stays data (`byte[]`), same
  treatment as Apple II's `Apple2_Video.rom` — it's a passive lookup table,
  not something whose *timing* does anything. Confirm whether Apple I's mask
  matches Apple II's dump byte-for-byte before assuming reuse (Apple I
  predates Apple II and is documented as uppercase-only).

## ROM

**Not sourced yet.** On real hardware this is two 256×4 bipolar PROMs
(`ICA1`, `ICA2`, schematic Note 11: Signetics 82S129 / Harris H1024 /
Intel-MMI 3601 are interchangeable here) forming one 256×8 image at
`$FF00`-`$FFFF` — for us, still just one 256-byte `byte[]` literal in a C#
file (`Emulation/Systems/AppleI/Roms/WozMonitor.cs` or similar), since the
two-PROM split is a hardware width limitation with no emulation
consequence.

This is possibly the most widely-reproduced 256 bytes in retrocomputing —
full disassemblies are published (e.g.
[SB-Projects' Woz Monitor page](https://www.sbprojects.net/projects/apple1/wozmon.php))
and the raw hex is easy to find. Either works: send over the 256 bytes
directly, or say so and it'll get pulled from a public disassembly/hex
listing at implementation time, with the same sourcing-disclaimer precedent
`AppleII/Roms/Apple2_Plus.rom` already has.

## Chip inventory (from the schematic, by designator)

### CPU

| Designator | Part | Notes |
|---|---|---|
| `ICA7` | `Mos6502Chip` | Already have. Board also supports a 6800 (jumper-selected) — out of scope, see above. |

### ROM / RAM

| Designator | Part | Qty | Notes |
|---|---|---|---|
| `ICA1`, `ICA2` | 256×4 PROM | 2 | Combined into one `byte[256]` Monitor ROM at $FF00-$FFFF (chip-select block `CSF`, jumper `Y`). |
| `ICA11`-`ICA18` | `MK4096` (4096×1 DRAM) | 8 | One 4K×8 bank, chip-select block `CS1` (jumper `W`) — $1000-$1FFF. |
| `ICB11`-`ICB18` | `MK4096` | 8 | Second 4K×8 bank, chip-select block `CS0` (jumper `X`) — $0000-$0FFF. Together: 8K RAM at $0000-$1FFF. |

Both banks share one `M0`-`M7` data bus (safe since their chip-selects are
mutually exclusive 4K blocks); `ICA9`/`ICA10` (`8T97` tri-state hex buffers,
already have `Ttl8T97Chip`) gate that shared bus onto the CPU's `D0`-`D7`.

### PIA

| Designator | Part | New file |
|---|---|---|
| `ICA4` | `MC6820` PIA | `Emulation/Chips/Mos6820/Mos6820Chip.cs` (+ `Debugging/`, `README.md`, following `Mos6532`'s shape). Chip-select block `CSD` (jumper `Z`) — $D000-$DFFF, matching the well-known $D010-$D013 `KBD`/`KBDCR`/`DSP`/`DSPCR` registers from the WozMon source. `PA0`-`PA7`/`CA1` = keyboard; `PB0`-`PB6` (nets `RD1`-`RD7`) = display data out to the terminal section; `CB2` = display handshake. |

### Address decode / bus

| Designator | Part | Qty | New file | Notes |
|---|---|---|---|---|
| `ICB9` | `74154` (4-to-16 decoder) | 1 | `Ttl74154Chip.cs` | The chip-select generator: 16× 4K-block selects `CS0`-`CSF`. `Y`→`CSF` (ROM), `Z`→`CSD` (PIA), `W`→`CS1`, `X`→`CS0` (RAM); `R`/`S`/`T` are user-selectable (expansion). |
| `ICB5`, `ICB6` | `74157`/`74S257` | 2 | Have `Ttl74157Chip` | DRAM row/column address multiplexer + `RAS`/`CAS` distribution to the `MK4096` array. |
| `ICB7`, `ICB8` | `74157`/`74S257` | 2 | Have `Ttl74157Chip` | Address-source mux; exact role (plain pass-through vs. DMA-source select) to confirm net-by-net in phase 2 — Note 13 only requires the tri-state `74S257` variant here if DMA is added, so in the base (no-DMA) config these should reduce to a fixed pass-through. |
| `ICA9`, `ICA10` | `8T97` | 2 | Have `Ttl8T97Chip` | RAM-data-bus-to-CPU-data-bus tri-state buffers (needed in the base config, not DMA-only). |
| `ICB3` | `74123` (dual monostable) | 1 | `Ttl74123Chip.cs` | Section A (480nsec): cassette-interface timing — out of scope. Section B (3.5μsec): keyboard/PIA display-handshake pulse — needed. |
| `ICB1`, `ICB2`, `ICC15` | `7400` (quad NAND) | 3 | Have `Ttl7400Chip` | Glue logic: `RAS`/`CAS` gating, bus-enable generation, IRQ/handshake combining. |

### Terminal section — counters, registers, gates

| Designator | Part | Qty | New file |
|---|---|---|---|
| `ICD7`, `ICD8`, `ICD9`, `ICD11`, `ICD15` | `74161` | 5 | Have `Ttl74161Chip` |
| `ICD6` | `74160` | 1 | Have `Ttl74160Chip` |
| `ICC7` | `74174` (hex D-FF) | 1 | Have `Ttl74174Chip` |
| `ICC13` | `74175` (quad D-FF, clear) | 1 | Have `Ttl74175Chip` |
| `ICC4`, `ICC14` | `74157` | 2 | Have `Ttl74157Chip` |
| `ICD1` | `74166` (shift register) | 1 | Have `Ttl74166Chip` — character-bitmap-to-serial-dot shift-out. |
| `ICD12` | `7404` (hex inverter) | 1 | Have `Ttl7404Chip` — all 6 gates used (crystal oscillator + misc). |
| `ICC10` | `7402` (quad NOR) | 1 | Have `Ttl7402Chip` |
| `ICC12` | `7408` (quad AND) | 1 | Have `Ttl7408Chip` |
| `ICC6` | `7410` (triple 3-in NAND) | 1 | `Ttl7410Chip.cs` — new |
| `ICC5` | `7427` (triple 3-in NOR) | 1 | `Ttl7427Chip.cs` — new |
| `ICC9` | `7432` (quad OR) | 1 | Have `Ttl7432Chip` |
| `ICC8` | `7450` (dual AND-OR-INVERT) | 1 | `Ttl7450Chip.cs` — new |
| `CR1`-`CR4` | 1N914 diodes | 4 | Simple enough to inline, not a chip class. |

### Terminal section — the character-memory delay line

| Designator | Part | Qty | New file |
|---|---|---|---|
| `ICD2` | `2513` character generator | 1 | Data (`byte[]`), not a chip — see Fidelity approach. |
| `ICC3` | `2519` (40×6 recirculating shift register) | 1 | `Signetics2519Chip.cs` — new, real shift/recirculate behavior. |
| `ICD4A`, `ICD4B`, `ICD5A`, `ICD5B`, `ICC11B`, `ICD14A`, `ICD14B` | `2504` (dynamic recirculating shift register) | 7 | `Signetics2504Chip.cs` — new, real shift/recirculate behavior. |
| `ICC11A` | `DS0025` (dual MOS clock driver) | 1 | `Ds0025Chip.cs` — new, two-phase clock generator for the 2504/2519 bank. |

### Composite video output

| Designator | Part | Notes |
|---|---|---|
| `Q5` + `R1`/`R2`/`R12` | Discrete NPN + resistor network | Video/sync summing stage, analogous to Apple II's Q3 — modeled the same way (`AppleISystem.CompositeVideo.cs`), monochrome (no burst input at all). |
| `ICD13` | `555` timer | Have `Ne555Chip` — ~2.2Hz cursor-blink oscillator (`R10`=10k, `R11`=10k, `C7`=22μF). |

### Explicitly not needed (confirmed from the schematic)

- `ICC1` (7404) + `Q1`-`Q4` + surrounding R/C — the 6800-only two-phase
  clock-shaping cluster (Note 7). Confirm the dashed-box boundary exactly in
  phase 2, but at minimum the four transistors and their R/C network are
  6800-only.
- The `74S257` tri-state variant of `ICB5`-`ICB8`, and whatever the `X`/`W`
  DMA-source-select logic on `ICB1:B`/`ICB2:A` turns out to gate — DMA is
  explicitly optional (Note 13), out of scope for v1.
- Any onboard keyboard-matrix encoder — confirmed there isn't one; see
  above.

## Composite video

Confirmed facts (Wikipedia, cross-checked against numbers this codebase
already uses for Apple II):

- CPU clock: **1.022727MHz**, exactly 2/7 of the NTSC color subcarrier
  (3.579545MHz) — the same 14.31818MHz crystal (4× the subcarrier, confirmed
  on the schematic as `ZQ1`, 14.31818MHz) that `AppleIISystem` ticks at,
  divided down the same way.
- **`AppleISystem` should tick at the full 14.31818MHz master rate**, same
  reasoning as Apple II: the CPU clock and dot clock are both synchronous
  divisions of the one oscillator, and preserving that phase relationship
  from the start avoids a rewrite later. Now that the actual schematic is in
  hand, phase 3 can trace the exact counter chain (`ICD6`-`ICD9`/`ICD11`/
  `ICD15`) from crystal to CPU `CLK0` directly, rather than guessing at
  whether the division is a clean ÷14 or needs an Apple-II-style long-cycle
  stretch.
- No color-burst gate signal exists on this board at all (confirmed, see
  above) — `AppleISystem.CompositeVideo.cs` is correspondingly simpler than
  Apple II's: a two-input (video bit, sync bit) sum-then-clamp table instead
  of a three-input one, no subcarrier phase tracking.

## Phased plan

**Phase 0 — Scaffolding**
`AppleISystem` skeleton, catalog/registry entries (`"applei"`), empty
`Debugging/AppleIDebugger.cs`. ROM in place per the ROM section. Nothing
functional yet.

**Phase 1 — New chip library**
`Mos6820Chip` (PIA), `Ttl74154Chip`, `Ttl74123Chip`, `Ttl7410Chip`,
`Ttl7427Chip`, `Ttl7450Chip`, and `Ds0025Chip`, each with real
datasheet-driven unit tests, independent of the Apple I board.

**Phase 2 — CPU/memory core, no video**
Wire `Mos6502Chip` + the two `MK4096` DRAM banks + the Monitor ROM +
`Mos6820Chip` through the real `74154`/`74157`/`8T97` address-decode chain.
Nail down `ICB7`/`ICB8`'s exact role while wiring this (see chip inventory).
PIA display-side handshake lines can be stubbed — no video yet.
**`AppleISystem.Tick()` ticks at the full 14.31818MHz master rate.**
**Done when:** the CPU runs the reset vector into WozMon and executes
correctly (debugger memory/register view + instruction trace).

**Phase 3 — Video timing chain**
Build the horizontal/vertical counter chain (`ICD6`-`ICD9`, `ICD11`,
`ICD15`) that generates composite sync, and trace the exact crystal→CPU
`CLK0` division ratio for real (see Composite video above) rather than
assuming it.

**Phase 4 — The character-memory delay line + text video + keyboard**
The centerpiece phase: `Signetics2504Chip` ×7 + `Signetics2519Chip`,
`Ds0025Chip`'s two-phase clock actually driving their recirculation, the
`74157`-based write-mux, and the cursor-column comparator logic
(`ICC5`-`ICC9`, `ICC7`, `ICC13`) that generates the `WRITE` pulse at the
right moment in the recirculation cycle. Plus the `2513` character
generator and `74166` pixel shift-out into a `DisplayBuffer`, and the PIA
keyboard wiring (`B4` connector straight to `PA0`-`PA7`/`CA1`, no encoder
chip). **Done when:** you can see and type at the WozMon `\` prompt, and
hand-type/run a short program — this is also where the recirculating
memory's write timing gets validated (typing near the right and wrapping to
a new line needs to actually work, not just look right on a static screen).

**Phase 5 (stretch) — Cassette interface**
The optional cassette card (uses `ICB3`'s currently-unused 480nsec 74123
section) — a separate expansion board, needs a known-good cassette audio
image to test against.

**Phase 6 (stretch) — 64K RAM expansion card**

**Phase 7 (stretch) — DMA**
Swap `ICB5`-`ICB8` for tri-state `74S257`, wire up whatever `ICB1:B`/
`ICB2:A`'s `X`/`W` select turns out to gate (per Note 13) — only worth doing
if some future expansion card actually needs it.

## Open risks

- The 7th `2504` (`ICC11B`)'s exact role isn't pinned down — six clearly
  match `2513`'s 6-bit character-code address, the seventh carries some
  extra bit through the same delay line. Resolve net-by-net in phase 4
  rather than guessing at its purpose ahead of time.
- `ICB7`/`ICB8`'s exact function (vs. `ICB5`/`ICB6`, which are clearly the
  DRAM row/column multiplexer) wasn't fully traced net-by-net from the
  rendered schematic tiles — confirm during phase 2 wiring.
- Confirm whether Apple I's `2513` mask matches Apple II's `Apple2_Video.rom`
  byte-for-byte before assuming reuse (see Fidelity approach).
- The cursor-column comparator (`ICC5`-`ICC9`, `ICC7`, `ICC13` driving
  `WRITE`) is the trickiest net cluster on the whole board and the one place
  most worth extra implementation time and testing, similar to how the
  Apple II plan flagged the `AY-5-3600`'s debounce/strobe/rollover behavior
  as its own small project.
