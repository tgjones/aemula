# NES PPU (Ricoh2C02) — pins-as-properties refactor

## Goal

Bring `Ricoh2C02Chip` in line with the pin model `Ricoh2A03Chip` already uses:
every hardware pin is a **property** on the chip (getter, setter, or both,
matching the real pin's direction), and the chip is driven purely by writing its
clock/strobe pins — no bespoke per-cycle entry points.

By the end:

- `Ricoh2C02Pins` (the public mutable struct) is **deleted**.
- `Ricoh2C02Chip.Tick()` is **gone** — the master-clock divider runs from a
  `Clk` pin setter, exactly like `Ricoh2A03Chip.Clk`.
- `Ricoh2C02Chip.CpuCycle()` is **gone** — the CPU-register read/write runs from
  the falling edge of a new `/DBE` (data-bus-enable / chip-select) pin setter,
  the same shape as `Ricoh2A03Chip.OnM2Rising()` running off the `Clk` divider.
- `NesSystem` talks to the PPU only through pin properties.

This is a behaviour-preserving refactor. No timing, render, or video-signal
logic changes; only how the chip is clocked and how its pins are exposed.

## Reference: how Ricoh2A03 does it

`Ricoh2A03Chip` (see `Emulation/Chips/Ricoh2A03/Ricoh2A03Chip.cs`) has no pins
struct. Instead:

- Backing fields grouped under a `// Pin values.` comment
  (`_address`, `_rw`, `_clk`, `_m2`, …).
- One property per pin. Direction matches hardware:
  - `Clk` — `get`/`set`; the setter is the whole clock engine. On each value
    change it bumps `_clockCounter`, and at divider boundaries it calls internal
    step methods (`OnM2Rising()`), toggles `_phi0`, and forwards the derived
    clock to the wrapped core.
  - `Address`, `RW`, `M2` — `get` only (chip drives them out); some fall through
    to the wrapped `_cpuCore`, some are `?? ` fallbacks for a DMA override.
  - `Data` — `get`/`set` (bidirectional bus).
  - `Nmi`, `Irq`, `Rdy` — `set` only (inputs).
- No `Cycle()` / `Tick()` method. `NesSystem.DoCpuCycle()` pulses
  `Cpu.Clk = false; Cpu.Clk = true;` once per master tick and then reads/writes
  `Cpu.Address` / `Cpu.Data` / `Cpu.RW`, gating its bus service on an *edge* of
  a chip output (`Cpu.CpuCorePhi2` rising).

We mirror all of this.

## Current 2C02 surface to replace

`Ricoh2C02Pins` fields (`Emulation/Chips/Ricoh2C02/Ricoh2C02Pins.cs`):

| Field | Real pin(s) | Dir (PPU's POV) | Becomes |
|---|---|---|---|
| `CpuRW` | R/W̄ | in | `CpuRw` — `set` |
| `CpuAddress` | RS0–RS2 | in | `CpuAddress` — `set` |
| `CpuData` | D0–D7 | bidir | `CpuData` — `get`/`set` |
| `PpuAddressData` (`.Address` / `.AddressHi` / `.Data` overlap) | AD0–AD7 (mux'd), A8–A13 | out addr / bidir data | `PpuAddressBus` — `get` (ushort), `PpuData` — `get`/`set` (byte) |
| `PpuAle` | ALE | out | `PpuAle` — `get` |
| `PpuRD` | R̄D̄ | out | `PpuRd` — `get` |
| `PpuWR` | W̄R̄ | out | `PpuWr` — `get` |
| `Nmi` | I̅N̅T̅ (→ CPU N̅M̅I̅) | out | `Nmi` — `get` |

New pins (currently implicit in `Tick()` / `CpuCycle()`):

| Pin | Real pin | Dir | Property |
|---|---|---|---|
| Master clock ~21.48 MHz | CLK | in | `Clk` — `get`/`set` (setter is the divide-by-4 dot-clock engine) |
| Chip select / data-bus enable | D̄B̄Ē (active low) | in | `Dbe` — `set` (falling edge = one register access) |

No reset pin — the real 2C02 has none, and the current code has none. Keep it
that way.

### The AD0–AD7 multiplex

The `[StructLayout(Explicit)]` `PpuAddressData` struct models the real
low-8-bits-shared-between-address-and-data multiplexing (`.Data` overlays the low
byte of `.Address`). That mechanism is still needed; only its *exposure* changes.

- Keep the overlap type, but demote it to a **private nested struct** of
  `Ricoh2C02Chip` (rename to `MultiplexedAddressData` for clarity; it is no
  longer a "pin"). Backing field `private MultiplexedAddressData _adBus;`
- `PpuAddressBus` getter returns `_adBus.Address` (14 meaningful bits).
  `NesSystem` uses this for `pa13` and the CHR/VRAM address.
- `PpuData` getter returns `_adBus.Data` (what the PPU drives on a write);
  setter stores into `_adBus.Data` (how `NesSystem` delivers a read byte back).
- Internally, `Ricoh2C02Chip.Render.cs` / `.cs` switch their
  `Pins.PpuAddressData.Address = …` / `Pins.PpuAddressData.Data` accesses to the
  private `_adBus` field directly.

## Steps

### 1. Introduce the pin properties on `Ricoh2C02Chip`

In `Ricoh2C02Chip.cs`, add a `// Pin values.` field block and the properties
above. For this step, keep `public Ricoh2C02Pins Pins;` in place and have the
new properties read/write the corresponding `Pins.*` field, so nothing breaks
mid-refactor and each later step is a small diff. (Same staging the 2A03 "Simplify
… pin" commits used.)

### 2. Replace `Tick()` with the `Clk` pin

- Add `private bool _clk;` and a divide-by-4 counter (reuse `_dotClockDivider`,
  or a fresh `_clkDivideCounter`).
- `public bool Clk { get => _clk; set { … } }` — setter mirrors
  `Ricoh2A03Chip.Clk`: ignore no-change writes; on every genuine transition bump
  the counter; when the counter completes one dot's worth of master-clock
  half-periods, call `CycleDot()` and wrap.
  - Math: `NesSystem` will drive `Clk = false; Clk = true;` once per master
    period ⇒ 2 transitions/period. One dot = 4 master periods = **8
    transitions**. (Today: `Tick()` once/period, `_dotClockDivider` 0→3, i.e.
    1 dot / 4 calls. Same ratio, counted in half-periods.)
- Delete `public bool Tick()`. Move its doc-comment intent onto `Clk`.
- `CycleDot()` stays exactly as-is (private, unchanged body).
- `SeedVideoState()` / `ResetVideoState()` already poke `_dotClockDivider = 0`;
  point them at whatever counter field survives.

### 3. Replace `CpuCycle()` with the `/DBE` pin

- Add `private bool _dbe = true;` (idle high — inactive).
- `public bool Dbe { set { if (_dbe == value) return; _dbe = value; if (!value) OnDbeActive(); } }`
  — falling edge (chip selected) performs the access.
- Rename `CpuCycle()` → `private void OnDbeActive()`. Body is unchanged except
  `Pins.CpuRW` → `_cpuRw`, `Pins.CpuAddress` → `_cpuAddress`,
  `Pins.CpuData` → the `CpuData` backing field, and the palette-write special
  case `Pins.PpuWR = true;` → `_ppuWr = true;`.
- The `ref var pins = ref Pins;` locals go away.

Note: on a read, `OnDbeActive()` leaves the result in the `CpuData` backing
field; `NesSystem` reads it back via the `CpuData` getter after the pulse — same
handshake as today (`Cpu.Data = ppuPins.CpuData;`).

### 4. Repoint the internal render/VRAM code

`Ricoh2C02Chip.cs` (`SetupVramRequest*`, `PpuRead`/`PpuWrite` paths) and
`Ricoh2C02Chip.Render.cs` (`BeginVramFetch` / `EndVramFetch` /
`BackgroundFetchTick` latches) currently read and write `Pins.PpuAddressData.*`,
`Pins.PpuAle`, `Pins.PpuRD`, `Pins.PpuWR`. Switch every one to the private
backing fields (`_adBus`, `_ppuAle`, `_ppuRd`, `_ppuWr`). No logic change.

### 5. Delete `Ricoh2C02Pins`

- Remove `public Ricoh2C02Pins Pins;` from `Ricoh2C02Chip.cs`.
- Delete `Emulation/Chips/Ricoh2C02/Ricoh2C02Pins.cs`.
- Move the `PpuAddressData` overlap struct into `Ricoh2C02Chip` as the private
  nested `MultiplexedAddressData` (step's "AD0–AD7 multiplex" note).

### 6. Update `NesSystem`

`Emulation/Systems/Nes/NesSystem.cs`:

- `Tick()`:
  - `DoPpuCycle()` no longer early-returns on a bool. Instead drive the clock:
    `Ppu.Clk = false; Ppu.Clk = true;` once per master tick (parallel to
    `DoCpuCycle`'s `Cpu.Clk` pulse).
  - `Cpu.Nmi = Ppu.Pins.Nmi;` → `Cpu.Nmi = Ppu.Nmi;`.
- `DoCpuCycle()`, `case 0b001:` (PPU ports): replace the
  `ppuPins.CpuRW = …; ppuPins.CpuAddress = …; ppuPins.CpuData = …; Ppu.CpuCycle();
  Cpu.Data = ppuPins.CpuData;` block with:
  ```
  Ppu.CpuRw = Cpu.RW;
  Ppu.CpuAddress = (byte)(address & 0x7);
  Ppu.CpuData = Cpu.Data;
  Ppu.Dbe = false;   // select — runs the access
  Ppu.Dbe = true;    // deselect
  Cpu.Data = Ppu.CpuData;
  ```
- `DoPpuCycle()` external-bus service: `ref var ppuPins = ref Ppu.Pins;` and the
  `ppuPins.*` accesses become the new getters/setter
  (`Ppu.PpuAle`, `Ppu.PpuAddressBus`, `Ppu.PpuRd`, `Ppu.PpuWr`, `Ppu.PpuData`).
  - `ppuPins.PpuAddressData.Data` (ALE-time low byte latch) →
    `(byte)Ppu.PpuAddressBus` while `Ppu.PpuAle` is set. `PpuAddressBus` exposes
    the full latched address; the low byte is `& 0xFF`, the high byte
    `>> 8`, matching the old `.AddressHi`.
  - Because the PPU only moves ALE/RD/WR on dot boundaries and the byte[] writes
    are idempotent across the 4 redundant master ticks, the level-driven service
    stays correct even though it now runs every tick. **Recommended:**
    edge-gate it anyway — act on `Ppu.PpuRd` / `Ppu.PpuWr` falling edges with
    `_lastPpuRd` / `_lastPpuWr` locals, mirroring the `cpuPhi2Rising` pattern in
    `DoCpuCycle()` — so rendering doesn't do 4× the array traffic.

### 7. Tests

- `Aemula.Tests/Emulation/Systems/Nes/Ricoh2C02Tests.cs` calls `chip.Tick()`
  (~4 sites). Replace each with `chip.Clk = false; chip.Clk = true;`
  (add a `private static void Master(Ricoh2C02Chip c) { c.Clk = false; c.Clk = true; }`
  helper in that file and call `Master(chip)` for readability). The comment
  "One behavioural Ricoh2C02Chip.Tick() = one master period = two cells" stays
  true — just rename it to the clock pulse.
- `NesSystemTelevisionTests.cs` uses `nes.Tick()` only — no change.
- No PPU pin field is touched anywhere else in the test project (`grep`
  confirmed).

### 8. Verify

Targeted runs (per the repo's "no full suite" rule):

- `Ricoh2C02Tests` — the Flawless2C02 lockstep; catches any dot-clock divider
  off-by-one from step 2.
- `NesSystemTelevisionTests` — full-system settle-to-palette; catches CPU
  register handshake / NMI regressions from steps 3 and 6.
- Build `Aemula.Console` / `Aemula.Benchmarks` (they compile against `NesSystem`
  but don't touch PPU pins) to confirm no stale references.

## Out of scope

- Sprite pipeline, EXT0–EXT3 pins, colour-emphasis — untouched.
- Making `Clk` reject external `get` (2A03 leaves a `// TODO: Shouldn't be
  accessible` getter; match that rather than solve it here).
- Any change to `CycleDot()` / `RenderTick()` / `UpdateVideoSignal()` internals.
