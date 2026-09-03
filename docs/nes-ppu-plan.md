# NES PPU — Fill-in Plan + Automated Test-ROM Harness

## Goal

Take `Ricoh2C02Chip` from "background-only raster + fully-calibrated composite
video" to a PPU that passes the community NTSC PPU test-ROM suites, and put an
**automated, self-checking** `NesSystemTests` harness in place so the
implementation can be driven test-first without a human in the loop.

The composite-video / NTSC-decode path (`Ricoh2C02Chip.Video.cs`,
`NesSystem.CompositeVideo.cs`) is already node-for-node calibrated against
`Flawless2C02` and is **out of scope** here except where a new PPU feature
(colour emphasis, grayscale) feeds it.

## Where the PPU is today

`Emulation/Chips/Ricoh2C02/`:

| File | What it does |
| --- | --- |
| `Ricoh2C02Chip.cs` | Pins, `$2000-$2007` register decode (`OnDbeActive`), `_v/_t/_x/_w` scroll regs, `$2007` read buffer + palette special-case, palette-address mirroring, VRAM request state machine. |
| `Ricoh2C02Chip.Registers.cs` | `PpuCtrl` / `PpuMask` / `PpuStatus` bitfield structs. |
| `Ricoh2C02Chip.Render.cs` | **Background only.** NT/AT/PT fetches over the multiplexed bus, the two pattern + two attribute shifters, `IncrementScrollHorizontal/Vertical`, coarse/fine copies at dots 256/257/280-304, background pixel mux. |
| `Ricoh2C02Chip.Video.cs` | Composite waveform DAC-tap state machine. Done. |

### Confirmed gaps (what the test ROMs will exercise)

**Sprites — entirely absent.**
- No secondary-OAM sprite evaluation (visible dots 65-256).
- No sprite pattern fetches (dots 257-320), no sprite shifters/counters.
- No sprite pixel mux, no BG/sprite priority, no left-column (`$2001` bit 2) clip.
- **No sprite-0 hit** (`$2002` bit 6).
- **No sprite overflow** (`$2002` bit 5), including the well-known buggy diagonal scan.
- No 8×16 sprites (`$2000` bit 5).

**VBL / NMI timing — approximate.**
- VBL set/clear are hard-pinned to `(241,1)` / `(261,1)`; the suites check these
  to 1-dot accuracy, plus:
  - Reading `$2002` on the exact dot VBL is set → returns 0 **and suppresses NMI**
    for that frame (`06-suppression`). Not modelled.
  - Enabling NMI (`$2000.7` 0→1) while the VBL flag is already set → NMI fires
    "after the NEXT instruction" (`04-nmi_control`, `05-nmi_timing`). The line is
    recomputed every dot so it half-works, but the CPU-side edge/latency nuance
    is untested.
  - Toggling NMI enable off/on across the set point (`07`, `08`).
- Odd-frame dot skip exists in `CycleDot`; `09/10-even_odd*` check its exact
  gating relative to enabling/disabling BG.
- No post-reset PPU "warm-up" (`$2000/$2001/$2005/$2006` writes ignored for
  ~29658 cycles after reset).

**Register-level quirks.**
- `$2004` read does **not** mask sprite-byte 2 with `$E3` (`oam_read`).
- No PPU **open-bus decay register** — `_currentLatchData` is a plain latch with
  no timed decay and refreshes on the whole read (`ppu_open_bus`).
- `$2007` read-buffer edge cases around `$3F00-$3FFF` mirroring / the "shadow"
  nametable read under palette (`vram_access`, `test_ppu_read_buffer`).
- Grayscale (`$2001.0`) only masks in `ReadPaletteMemory`; colour emphasis
  (`$2001.5-7`) is unmodelled (documented in `Video.cs`).

**Cartridge bus plumbing that blocks the ROMs from even reporting.**
All of this is *cartridge* behaviour that currently lives inline in
`NesSystem.DoCpuCycle` / `DoPpuCycle` behind `// TODO: Mapper implementations`.
Phase 0 replaces it with a pin-level `Cartridge` object (CPU + PPU connector
pins) delegating bank/mirroring/IRQ decisions to a `Mapper` strategy — the same
"chip drives pins" bet the rest of the codebase makes.
- CPU ROM read is `_cartridge.PrgRom[address & 0x3FFF]` — hard-wired
  NROM-**128**. NROM-256 (32 KB) ROMs read mirrored garbage. Nothing computes
  `/ROMSEL`; nothing decodes `$6000-$7FFF`.
- No **CHR RAM**: `DoPpuCycle` reads `_cartridge.ChrRom[...]` and drops CHR
  writes. Every 2005-era blargg test and the sprite suites use CHR RAM.
- No **name-table mirroring**: `_vram[ppuAddress & 0x7FF]` ignores the fact that
  mirroring *is* the cartridge driving the `CIRAM A10` / `CIRAM /CE` connector
  pins each PPU access (`Cartridge.Flags6.NametableMirroring` is `private` and
  unused).
- No **cartridge WRAM at `$6000-$7FFF`** — the modern blargg result protocol
  writes its status / `$DE $B0 $61` / text there. WRAM presence is
  per-board, so it belongs to the `Mapper`/`Cartridge`, **not** `NesSystem`.
- No cartridge **`/IRQ`** pin (open-collector, wire-AND with the 2A03's IRQ
  sources). Unused by the PPU ROMs but part of the connector.
- `Mapper` / `Mapper000` are empty stubs; `Mapper.Create` only knows mapper 0.

## Test harness: `Aemula.Tests/Emulation/Systems/Nes/NesSystemTests.cs`

### Three result oracles

**Oracle A — modern blargg protocol (`$6000`).** Confirmed in
`ppu_vbl_nmi/source/common/text_out.s`:
- `$6000` = `$80` while running, `$81` = "needs reset (≥100 ms later)",
  `$00-$7F` = finished, value is the result code (`0` = pass).
- `$6001-$6003` = `$DE $B0 $61` signature (only trust `$6000` once this matches).
- `$6004…` = NUL-terminated ASCII log (surface it verbatim on failure).

Runner: tick until signature present **and** `$6000 < $80`, with a
cycles cap (≈ 20 s worth) that fails loudly. On `$81`, hold ≥100 ms of emulated
time then pulse `Cpu.Res` low/high and continue.

**Oracle B — name-table text scrape (2005-era suites).** These have no
documented RAM status byte in the public sources ("runs on a custom setup"),
but they render their verdict with an **ASCII-mapped CHR font** (tile id ==
char code — see `sprite_hit .../runtime/console.a`), then spin in `forever`.
Runner: tick until the CPU is parked in a tight self-loop (PC stable / 3-byte
`JMP self` for many cycles) or a frame cap is hit, then read name-table RAM via
a debug hook, map tiles → ASCII, and assert the text contains `PASSED` and not
`FAILED`. Cross-check the zero-page `result` byte (`$F8` in
`sprite_hit .../runtime/validation.a`) when present.

**Oracle C — framebuffer hash (visual ROMs).** Add an optional raw RGB output
to the PPU (see Phase 5) and CRC the 256×240 frame after N frames, comparing to
a checked-in golden hash generated once and eyeballed against a reference
emulator screenshot.

### Plumbing the harness needs (Phase 0)

#### A. `Cartridge` becomes a pin-level connector object

Real hardware has no "does this address belong to the cart?" call — the
72-pin connector is wires, and a few dedicated select/timing pins plus decode
logic *inside* the cart answer it. Model that: `Cartridge` gets the connector
pins, `NesSystem` wires the 2A03 / 2C02 / mainboard to them each tick, and a
`Mapper` strategy object owns the bank / mirroring / IRQ decisions.

**CPU-side connector pins** (`Cartridge`):

| pin | dir | notes |
| --- | --- | --- |
| `CpuAddress` (A0-A14) | in | 15 lines — **A15 is not on the connector** |
| `CpuData` (D0-D7) | in/out | cart drives it only when it decodes a read it owns |
| `CpuRw` (R/W̄) | in | |
| `RomSel` (/ROMSEL) | in | mainboard-generated `!(A15 & M2)`; low ⇒ CPU is in `$8000-$FFFF` this cycle. The cart's only "PRG is mine" signal. |
| `M2` (φ2) | in | timing/enable; the cart qualifies its own `$6000-$7FFF` decode with it |
| `Irq` (/IRQ) | out | open-collector, wire-AND with the 2A03's IRQ sources |

The cart decodes internally: `/ROMSEL` low ⇒ drive PRG onto `CpuData`; else its
own WRAM gate (`M2 & A13 & A14`, A15 implicitly low) ⇒ drive WRAM; else leave
`CpuData` untouched so **open bus** (last value on the bus) falls out for free
instead of a synthetic 0.

**PPU-side connector pins** (`Cartridge`):

| pin | dir | notes |
| --- | --- | --- |
| `PpuAd` (AD0-AD7) | in/out | multiplexed low address byte / CHR data |
| `PpuA` (A8-A13) | in | high address bits |
| `PpuAle` | in | address-latch enable |
| `PpuRd` (/RD), `PpuWr` (/WR) | in | |
| `CiramA10` | out | **this pin is name-table mirroring**: cart ties it to PA10 (vertical), PA11 (horizontal), a fixed level (single-screen), or a mapper register (MMC1/MMC3) |
| `CiramCe` (/CE) | out | chip-enable for the mainboard's 2 KB name-table SRAM; NROM drives it from `/PA13`, mapper boards can gate it (CHR in `$2000-$3FFF`, 4-screen) |

**Faithful bus mux (decided):** the cart re-latches the low address byte from
`PpuAd` on `PpuAle` itself, exactly as `Ricoh2C02Chip.MultiplexedAddressData`
does on the other end — `NesSystem` does **not** hand it a pre-demuxed 14-bit
address. `NesSystem` keeps its own ALE latch only for its CIRAM lookup.

**`NesSystem`'s remaining job** — console-side hardware only, no per-mapper
branch ever:
1. Drive `Cartridge` CPU pins from `Cpu.Address/Data/RW`; compute `RomSel` on
   the "mainboard" from `Cpu.Address` bit 15 and the M2/φ2 phase.
2. `$0000-$1FFF` internal RAM, `$2000-$3FFF` → PPU register pins,
   `$4016/$4017` controllers, `$4014` (2A03-internal) as today. Everything
   else on the CPU bus is the cart.
3. After `Ppu.Clk`, feed `Cartridge` PPU pins from the 2C02 pins; read back
   `CiramCe` / `CiramA10`. If `CiramCe` asserted → the mainboard SRAM
   (`_ciram[(CiramA10 << 10) | (addr & 0x3FF)]`, replacing
   `_vram[addr & 0x7FF]`); else the cart drove `PpuAd` itself.
4. `Cpu.Irq = apuIrq & Cartridge.Irq` (active-low wire-AND).

**`Mapper` strategy surface** (consulted by `Cartridge`, no pins of its own).
The mapper owns *all* cartridge memory — PRG ROM, CHR ROM-or-RAM, WRAM, bank
registers — and exposes only behaviour; where those bytes live, how big they
are and whether a region is enabled never leak to the connector:
- `byte? CpuRead(ushort addr)` / `void CpuWrite(ushort addr, byte data)` — the
  whole cartridge CPU space ($4020–$FFFF). `null` from a read ⇒ not driving
  (open bus). `CpuWrite` covers WRAM and register writes both.
- `byte ChrRead(ushort addr)` / `void ChrWrite(ushort addr, byte data)` — the
  pattern tables ($0000–$1FFF); `ChrWrite` is a no-op on a CHR-ROM board.
- `bool CiramCe(ushort ppuAddr)` / `bool CiramA10(ushort ppuAddr)` — the two
  mirroring pins, so runtime-selectable mirroring is just a mapper register.
- `byte PeekCpu` / `void PokeCpu` / `byte PeekChr` — side-effect-free debug
  access (`PokeCpu` writes RAM only, never a register).
- `NametableMirroring Mirroring { get; }` — current wiring, for a debugger view.
- later: `bool Irq { get; }` (+ a per-PPU-cycle / per-M2 tick hook for counters).

A `protected abstract BankedMapper : Mapper` holds the flat-image plumbing the
discrete-logic boards share (one PRG array + size mask, one CHR array + RAM
flag, optional 8 KB WRAM); its fields are `private`, and subclasses only
override `PrgOffset` / `ChrOffset` / `WriteRegister`.

#### B. `Mapper000` (NROM)

- PRG: 16 KB mirrored across `$8000-$FFFF`, or 32 KB straight — from the
  header PRG size (kills the `& 0x3FFF` bug).
- CHR: 8 KB CHR ROM, or 8 KB **CHR RAM** (writable) when header CHR size == 0.
- WRAM: allocates 8 KB. The iNES `Flags6.ContainsPrgRam` bit is unreliable on
  these test builds and every mainstream emulator gives NROM its 8 KB
  regardless — so `Mapper000` does too, but it is **`Mapper000`'s call**. A
  mapper for a board with no WRAM just doesn't allocate it, and the cart's
  `$6000-$7FFF` decode then drives nothing (open bus).
- `CiramA10` = PA10 or PA11, fixed from `Flags6.NametableMirroring` (expose it —
  currently `private` on `Cartridge`); `CiramCe` = `!PA13`.

#### C. Other mappers — same shape, each a `Mapper` subclass

`NesSystem` and `Cartridge` do not change for any of these:
- `Mapper002` (UNROM) — 16 KB switchable + 16 KB fixed PRG bank, CHR RAM.
- `Mapper003` (CNROM) — CHR-ROM bank switch. Needed for `test_ppu_read_buffer`.
- `Mapper001` (MMC1) — PRG/CHR banking, runtime `CiramA10` from a register, its
  own PRG-RAM enable/disable. Needed for the combined multi-ROM builds
  (`ppu_vbl_nmi.nes` &c.).
- `Mapper.Create` switches on the number and throws a clear
  "mapper N not implemented" for the rest.

#### D. Debug peeks for the oracles

`NesSystem.PeekCiram(ushort)` + `Cartridge.PeekCpu(ushort)` / `PeekPpu(ushort)`
(side-effect-free, no pin movement) delegating to `Mapper.MapPrg/MapChr` and the
WRAM array. `InternalsVisibleTo Aemula.Tests` is already set.

#### E. Headless step

A path that skips `TickCompositeVideo` — the FIR decode is the measured hot
cost and the ROM oracles never read the Television. `NesSystem.TickHeadless()`
or a `bool DecodeVideo { get; set; }` gate.

#### F. ROM assets

Copy the ~18 chosen `.nes` files into
`Aemula.Tests/Emulation/Systems/Nes/Assets/nes-test-roms/<suite>/…` (already
glob-copied to output by the test `.csproj`). Total < 1 MB. Add a short
`PROVENANCE.md` pointing at `github.com/christopherpow/nes-test-roms`.

#### G. Test layout

One TUnit class per suite, each ROM a `[Test]` / parameterised case, so runs
stay targetable with `--treenode-filter` (full suite is ~40 min — never run
whole).

## Representative test ROMs (the motivating set)

Chosen for coverage spread and signal-to-noise. Paths are relative to
`/Users/timjones/Code/nes-test-roms`. All are mapper 0 unless noted.

| # | ROM | Oracle | Drives / proves |
| --- | --- | --- | --- |
| 1 | `blargg_ppu_tests_2005.09.15b/vram_access.nes` | B | `$2007` read buffer: 1-byte delay, unaffected by writes, palette read fills buffer from NT underneath |
| 2 | `blargg_ppu_tests_2005.09.15b/palette_ram.nes` | B | Palette RAM r/w, `$3F00-$3FFF` mirroring, `$10/$14/$18/$1C`↔`$00…` mirrors, non-buffered palette read |
| 3 | `blargg_ppu_tests_2005.09.15b/sprite_ram.nes` | B | `$2003`/`$2004` r/w + increment rules, `$2004` read no-increment, `$4014` DMA start/wrap/leaves `$2003` |
| 4 | `blargg_ppu_tests_2005.09.15b/vbl_clear_time.nes` | B | VBL flag cleared ≈2270 CPU clocks after NMI (coarse clear-timing) |
| 5 | `ppu_vbl_nmi/rom_singles/01-vbl_basics.nes` | A | VBL period length, `$2002` mirrors every 8 bytes, flag-clear-on-read, BG-off period |
| 6 | `ppu_vbl_nmi/rom_singles/02-vbl_set_time.nes` | A | Exact PPU dot the VBL flag is set (1-dot table) |
| 7 | `ppu_vbl_nmi/rom_singles/03-vbl_clear_time.nes` | A | Exact PPU dot the VBL flag is cleared (1-dot table) |
| 8 | `ppu_vbl_nmi/rom_singles/04-nmi_control.nes` | A | NMI on enable-while-set, `$2000` mirroring, no double-NMI on re-write `$80`, "after NEXT instruction" |
| 9 | `ppu_vbl_nmi/rom_singles/05-nmi_timing.nes` | A | NMI delivery latency vs. instruction boundary (1-dot table) |
| 10 | `ppu_vbl_nmi/rom_singles/06-suppression.nes` | A | Reading `$2002` at the set point returns 0 and suppresses that frame's NMI |
| 11 | `ppu_vbl_nmi/rom_singles/09-even_odd_frames.nes` | A | Odd-frame pre-render dot skip vs. BG-enable pattern (`00 01 01 02`) |
| 12 | `sprite_hit_tests_2005.10.05/01.basics.nes` | B | Sprite-0 hit: fires behind BG, misses when either layer off / transparent / other sprites |
| 13 | `sprite_hit_tests_2005.10.05/02.alignment.nes` | B | Pixel-exact hit alignment of sprite vs. BG on all four edges |
| 14 | `sprite_hit_tests_2005.10.05/09.timing_basics.nes` | B | `$2002` bit 6 set at the right dot within the scanline |
| 15 | `sprite_overflow_tests/1.Basics.nes` | B | Overflow set on 9th sprite/line, not cleared by read, cleared at VBL end, respects `$2001` |
| 16 | `oam_read/oam_read.nes` | A | `$2004` reads OAM at current `$2003` without incrementing; byte-2 `$E3` mask |
| 17 | `ppu_open_bus/ppu_open_bus.nes` | A | PPU 8-bit decay register: which bits each `$2000-$2007` read refreshes vs. returns stale |
| 18 | `full_palette/full_palette.nes` (32 KB PRG + CHR ROM) | C | End-to-end: all 64 NES colours on screen at once — framebuffer hash |

Stretch / later (bigger dependencies, add once the core is green):

| ROM | Needs |
| --- | --- |
| `ppu_vbl_nmi/rom_singles/{05b,07,08,10}` | finish the NMI/odd-frame tables |
| `sprite_hit_tests_2005.10.05/{03..08,10,11}` | corners, flip, clip, screen-bottom, 8×16, timing-order, edge-timing |
| `sprite_overflow_tests/{2..5}` | overflow details + the hardware scan bug + timing |
| `oam_stress/oam_stress.nes` | ~30 s run; only 1 of 4 power-up alignments passes — pin the alignment or mark expected-flaky |
| `ppu_read_buffer/test_ppu_read_buffer.nes` | **mapper 3 (CNROM)** + very long run |
| `blargg_ppu_tests_2005.09.15b/power_up_palette.nes` | exact power-up palette bytes — treat as informational, likely `2` (differs) |
| `scanline/scanline.nes`, `nmi_sync/demo_ntsc.nes` | mid-frame scroll / precise NMI cadence — visual (Oracle C) |
| combined `ppu_vbl_nmi.nes` (MMC1) | mapper 1 |

## Implementation phases

Each phase lands with its ROMs flipping green in `NesSystemTests`.

### Phase 0 — harness + cartridge/mapper plumbing
Everything under "Plumbing the harness needs" above: the pin-level `Cartridge`
+ `Mapper` refactor (A-D), the headless step (E), assets (F), test layout (G).
No `Ricoh2C02Chip` behaviour changes (debug peeks only). Checkpoint: `Mos6502`
/ `Ricoh2A03` and the existing NES tests (`NesSystemTelevisionTests`,
`Ricoh2C02Tests`, `NesControllerTests`) still green with the cartridge bus
logic moved out of `NesSystem` and onto the connector pins.
**Green after this:** every ROM boots far enough to report through its oracle.
In practice `oam_read` (16) and `01-vbl_basics` (5) already report code 0 on the
current crude VBL timing; `vram_access` (1) reports a real `$2007`-buffer bug
(`$03`) for Phase 1. Everything else is deferred to its phase.

### Phase 1 — register & VRAM correctness
- `$2004` read `$E3` mask on byte 2; confirm no-increment on read, increment on write.
- PPU open-bus **decay register**: 8 independent bits, refresh-on-write (all 8),
  per-register refresh mask on read (table in `ppu_open_bus/readme.txt`), decay
  to 0 after ~600 ms of not being driven to 1. Drive `_cpuData` from it for
  unreadable bits.
- `$2007` buffer: audit against `vram_access` items 4-7 (buffer untouched by
  write / palette write; palette read still loads buffer from the NT address
  "underneath"; grayscale/emphasis do not corrupt the shadow read).
- Palette: re-verify `GetPaletteAddress` mirroring covers `$3F04/$3F08/$3F0C`
  read-back and the `$3F1x` writes.
**Green:** 1, 2, 16, 17. **3 (`sprite_ram`)**: PPU-side subtests (2-5:
`$2003`/`$2004` r/w, no-increment-on-read, increment-on-write, `$E3` mask) all
pass; subtest 6 onward needs `$4014` OAM DMA, which is a pre-existing
`Ricoh2A03Chip` bug (the DMA unit never leaves `Pending` because its start
condition — `_cpuCore.RW && _cpuCore.Rdy` in `OnM2Rising` — is never met while
the core is RDY-stalled, so the CPU hangs at the `STA $4014`). Its own fix; the
test is `[Skip]`-marked with this note.

### Phase 2 — VBL / NMI timing
- Model VBL set at `(241,1)` and clear at `(261,1)` as the reference points, then
  add the ±1-dot race behaviour:
  - `$2002` read on the set dot: return bit 7 = 0, set an internal
    "suppress NMI this frame" flag, do not raise the NMI edge.
  - Reads 1 dot before/after per the `02`/`06` tables.
- NMI as an **edge** to the 2A03: latch `nmi_occurred = VBlankStarted`,
  `nmi_output = nmi_occurred && EnableNmi`; pull `/NMI` on the 1→0… actually on
  `nmi_output` rising; deliver to the CPU with the correct instruction-boundary
  latency (coordinate with `Ricoh2A03Chip` — `05-nmi_timing` is really a CPU+PPU
  joint test).
- Enable-while-set → schedule the edge for after the next CPU instruction.
- Re-writing `$80` to `$2000` while already enabled must not re-edge.
- Odd-frame skip: verify gating is "rendering enabled at `(261,339)`" exactly and
  that `09/10` see `00 01 01 02` / `08 08 09 07`.
- Post-reset warm-up: ignore `$2000/$2001/$2005/$2006` writes and hold `_w` until
  ~29658 PPU cycles after `Res`.
**Green:** 4, 5, 6, 7, 8, 9, 10, 11 (+ stretch `07`, `08`).

### Phase 3 — sprite pipeline + sprite-0 hit
New file `Ricoh2C02Chip.Sprites.cs`:
- 32-byte secondary OAM; sprite evaluation dots 65-256 (the real state machine,
  incl. the copy-and-compare `n/m` walk so overflow timing falls out in Phase 4).
- Sprite pattern fetches dots 257-320 (8 slots × NT-garbage + 2 PT reads), 8×8
  and 8×16 (`$2000.5`, tile-bank from bit 0 of tile id).
- 8 sprite shift registers + X counters + attribute latches; per-dot sprite mux
  active dots 1-256.
- Priority + `$3F10` sprite-palette select; left-8px clip (`$2001.2`);
  sprites disabled (`$2001.4`).
- **Sprite-0 hit:** set `$2002.6` when a non-zero sprite-0 pixel and a non-zero
  BG pixel coincide, respecting: both layers enabled, not at x=255, not in the
  clipped left 8 px if either left-clip bit is 0, once per frame, cleared at
  `(261,1)`.
**Green:** 12, 13, 14 (+ stretch sprite-hit `03-08,10,11`).

### Phase 4 — sprite overflow
- Set `$2002.5` when a 9th in-range sprite is found on a line during evaluation.
- Reproduce the **hardware bug**: after 8 sprites are found, the evaluation
  pointer increments `m` (byte) as well as `n` (sprite), scanning diagonally, so
  it produces false positives/negatives — `sprite_overflow_tests/{2,4}` check
  this precisely. `3.Timing` checks the dot it is set on.
**Green:** 15 (+ stretch overflow `2,3,4`; `5.Emulator` is intentionally
emulator-behaviour — may stay red).

### Phase 5 — visual output + colour path
- Add `Ricoh2C02Chip` raw framebuffer: per visible dot write
  `GetColor(paletteAddr)` (post BG/sprite mux) into a `Color[256*240]`; expose
  `ReadOnlySpan<Color> Framebuffer` + a frame-complete signal.
- Wire grayscale + colour emphasis into both the framebuffer colour and the
  `SetActivePicture` DAC path (emphasis = the ~120°-wide chroma pull-down noted
  in `Video.cs`).
- Oracle C golden-hash tests for `full_palette` (+ stretch `scanline`,
  `nmi_sync`).
**Green:** 18 (+ stretch visuals).

## Self-driven iteration loop

For each phase, per ROM:
1. Add the `[Test]` (ROM path + expected code / golden hash) — it fails.
2. Run just that class: `dotnet test src/Aemula.Tests --treenode-filter '/*/*/NesSystemTests/*<name>*'`.
3. On failure, dump Oracle A's `$6004` text or Oracle B's scraped name-table
   text — both are human-readable ("`NMI should occur when enabled…`"), so the
   failing sub-test names the missing behaviour directly.
4. Implement, re-run that class, then re-run the whole `NesSystemTests` class
   (seconds, not the 40-min full suite) to catch regressions.
5. Keep `Ricoh2C02Tests` (Flawless2C02 lockstep) and `NesSystemTelevisionTests`
   green throughout — Phase 2's timing shifts and Phase 5's emphasis/grayscale
   are the likely disturbers.

## Risks / notes

- **PPU/CPU sub-cycle alignment.** `05-nmi_timing`, `06-suppression`,
  `sprite_hit .../09` need the 2C02 dot clock and the 2A03 φ correct to one PPU
  clock. `NesSystem.Tick` already feeds both chips the 21.48 MHz master; the risk
  is *phase* (which master tick is dot 0 vs. which is φ2 rising). Budget time to
  pin this against `02-vbl_set_time`'s table, which is precisely a phase ruler.
- **Power-up alignment.** Real hardware has 1-of-4 random PPU/CPU alignments;
  several ROMs only pass on some. Pick a fixed deterministic alignment at
  construction and, where a ROM's readme says "passes for one alignment",
  assert that one (document it).
- **`result` address for Oracle B is inferred** (`$F8`) from the sprite-hit
  runtime, not the PPU-suite sources. The name-table scrape is the primary
  Oracle B path for exactly this reason; the `$F8` check is a cross-check only.
- **Framebuffer golden hashes** need a one-time trustworthy reference
  (Mesen/FCEUX screenshot) — generate, eyeball, check in with the screenshot
  alongside.
- Colour emphasis touching `Video.cs` is the one place this plan reaches into the
  calibrated composite path; keep it behind the existing tap structure and
  re-run `Ricoh2C02Tests`.
- **Phase 0 is bigger than it looks.** The pin-level `Cartridge` refactor moves
  live bus logic that `Mos6502ChipTests` / `Ricoh2A03ChipTests` / the NES
  television + controller tests all depend on transitively. Land it as its own
  reviewable change with those suites green *before* touching PPU behaviour.
- **Faithful PPU bus mux cost.** Having `Cartridge` re-latch AD0-7 on ALE means
  the CHR read path now has the same two-phase (address dot / data dot)
  handshake the 2C02 already runs — get the phase wrong and every CHR fetch is
  off by a dot. `full_palette` (static screen, Oracle C) is the cheapest thing
  to bring up first as a mux sanity check before the timing-sensitive ROMs.
