# Zilog Z80 — Implementation Plan

## Goal

Add a pin-level, **half-cycle-accurate** `Z80Chip` to Aemula, plus a unit-test
suite driven by well-known open-source Z80 test ROMs. No emulated system is
built in this pass — the deliverable is the chip and its tests only.

The Intel 8080 (`Emulation/Chips/Intel8080/Intel8080Chip.cs`) is the closest
existing thing and is the reference for the *pin-style API and state-machine
shape*. **No code is shared between the 8080 and the Z80** — the Z80 gets its
own registers, flags, parity table, ALU, and opcode tables. Only design
patterns are reused:

- Union register structs (`[StructLayout(LayoutKind.Explicit)]` with
  `Value`/`Hi`/`Lo` field overlaps — see `Intel8080/Registers.cs`).
- A big `switch (_ir) { case 0xNN: switch (cycleKey) { ... } }` microcode
  dispatch (`HandleInstruction`).
- Cycle transitions *staged* by `SetNextCycle` and *applied* on the next
  clock edge that begins a machine cycle (`_pendingMachineCycleType` /
  `ApplyPendingCycleTransition`).
- An `int` cycle key built from `(machineCycleType << 8) | tState` with named
  `const int` combinations (`FetchT1`, `MemoryReadT3`, …) for readable
  `case` labels.
- `/WAIT` handled the way the 8080 handles `READY`: sample on one specific
  clock edge into a latch, consult the latch on the edge that would otherwise
  advance the T-state.

## File layout

Follows the same vendor-part-folder convention as `Intel8080/`, `Mos6502/`,
`Ricoh2C02/` (a chip gets its own folder once it has companion files —
multiple partials, `Debugging/`, `UI/`).

```
src/Aemula/Emulation/Chips/Z80/
  Z80Chip.cs                 // pins, CLK setter (the half-cycle engine), state machine, SetNextCycle
  Z80Chip.Registers.cs       // (or Registers.cs) union structs: AF BC DE HL IX IY SP PC WZ + alt set
  Z80Flags.cs                // S Z Y H X P/V N C, AsByte/SetFromByte
  Z80Chip.Parity.cs          // Z80's own 256-entry parity table (NOT shared with 8080)
  Z80Chip.Alu.cs             // add8/adc8/sub8/sbc8/and/or/xor/cp, inc8/dec8, daa, add16/adc16/sbc16, rotates/shifts, bit/set/res, rrd/rld
  Z80Chip.Decode.Base.cs     // unprefixed opcode microcode
  Z80Chip.Decode.CB.cs       // CB-prefixed (rot/shift, BIT/SET/RES)
  Z80Chip.Decode.ED.cs       // ED-prefixed (block ops, 16-bit LD, SBC/ADC HL, NEG, IM, RRD/RLD, LD A,I/R, RETI/RETN, block I/O)
  Z80Chip.Decode.Index.cs    // DD/FD (IX/IY, incl. IXH/IXL/IYH/IYL undocumented) and DD CB / FD CB
  Debugging/
    Z80Disassembler.cs       // : Aemula.Debugging.Disassembler, mirrors Intel8080Disassembler (optional but recommended — see below)
  README.md                  // manuals + reference implementations, like Intel8080/README.md
```

```
src/Aemula.Tests/Emulation/Chips/Z80/
  Z80ChipTests.cs            // CP/M-harness conformance runner (ZEXDOC/ZEXALL/z80test)
  Z80ChipBusTimingTests.cs   // FUSE per-instruction bus-cycle timing suite
  Z80ChipWaitStateTests.cs   // /WAIT-driven Tw insertion, mirrors Intel8080ChipWaitStateTests
  Z80ChipInterruptTests.cs   // NMI / INT modes 0-2 / IFF1-2 / EI-delay / HALT (hand-written)
  Assets/
    zexdoc.com  zexall.com
    z80full.com z80doc.com z80flags.com z80ccf.com z80memptr.com
    tests.in  tests.expected            // FUSE test data (plain text)
```

The test project already globs `Content Include="Emulation\**\Assets\**\*.*"`
with `CopyToOutputDirectory=PreserveNewest`, so dropping the ROMs and data
files in `Assets/` is all the wiring needed. Test framework is **TUnit**
(`[Test]`, `[Arguments]`, `await Assert.That(...)`), same as the 8080 tests.

Namespace: `Aemula.Emulation.Chips.Z80`. Class: `Z80Chip`.

## Half-cycle-accuracy model

The 8080 has two non-overlapping external clock inputs (`Phi1`, `Phi2`); the
existing code puts T-state-boundary work on the `Phi1` rising edge and
mid-T-state bus work on `Phi2`. The Z80 has **one** clock input, `CLK`, and a
T-state is one full `CLK` period. "Half-cycle accurate" here means: **the chip
does work on every `CLK` edge — rising *and* falling — so that every bus signal
transition lands on the same half-T-state edge it does on real silicon.**

So the public clock API is a single pin, driven exactly like `Mos6502Chip.Phi0`
and `Ricoh2C02Chip.Clk`:

```csharp
public bool Clk
{
    set
    {
        if (_clk == value) return;   // ignore no-change writes
        _clk = value;
        if (value) OnClkRising();     // T-state begins: apply staged cycle transition, drive /M1 //MREQ //RD //WR //RFSH, address bus
        else       OnClkFalling();    // mid-T-state: sample /WAIT, latch read data, drive /WR data, run internal ALU steps
    }
}
```

A consumer clocks one T-state as `chip.Clk = true; chip.Clk = false;` (two
half-cycles), matching the 8080 test's four writes per T-state
(`Phi1↑ Phi1↓ Phi2↑ Phi2↓`).

### Where the edges matter (the map to bake into `Z80Chip.cs` as a comment)

From the Zilog Z80 CPU User Manual timing diagrams. "T2↓" = falling edge of T2.

| Machine cycle | Event | Edge |
|---|---|---|
| M1 (opcode fetch) | `/M1` low, address = PC, `PC++` | T1↑ |
| M1 | `/MREQ` low, `/RD` low | T1↓ |
| M1 | `/WAIT` sampled | T2↓ (and every Tw↓) |
| M1 | opcode latched from data bus into `_ir` | T3↑ |
| M1 | `/MREQ` `/RD` high; `/M1` high; `/RFSH` low, address = I·256+R, R7-bit increment | T3↑ / T3↓ |
| M1 | `/MREQ` pulses low for refresh | T3↓ … T4↑ |
| Memory read (M2+) | address driven | T1↑ |
| Memory read | `/MREQ` `/RD` low | T1↓ |
| Memory read | `/WAIT` sampled | T2↓ (+ Tw↓) |
| Memory read | data latched | T3↑ |
| Memory write | address driven | T1↑ |
| Memory write | `/MREQ` low; data driven | T1↓ |
| Memory write | `/WR` low | T2↓ |
| Memory write | `/WAIT` sampled | T2↓ (+ Tw↓) |
| Memory write | `/WR` `/MREQ` high | T3↓ |
| I/O read/write | `/IORQ` (+ `/RD` or `/WR`) low | T2↑ |
| I/O | one wait state **Tw always auto-inserted** between T2 and T3 | — |
| I/O | `/WAIT` sampled | Tw↓ |
| Interrupt ack (M1 special) | `/M1` low, `/IORQ` low (instead of `/MREQ`), **two** Tw auto-inserted | T2↓… |
| NMI | edge-triggered: falling edge on `/NMI` latched any time; acknowledged at end of current instruction | — |
| INT | level-triggered: `/INT` sampled at the **last T-state** of the current instruction (if `IFF1` and not EI-shadowed) | last T↓ |

Internal (no-bus) T-states — the extra clocks Z80 spends on 16-bit arithmetic,
`(IX+d)` displacement add, block-instruction repeat, `PUSH` predecrement, etc.
— are their own machine-cycle type (mirrors the 8080's `Dad` special cycle)
and do their register work on a chosen edge, with no external pin activity.

### `/WAIT` handling

Mirror the 8080's `READY`/`_readySampledLow` pattern exactly, on the Z80's
edge: on the `CLK` falling edge of T2 (and any already-inserted Tw), latch
`_waitSampledLow = !Wait`; on the next `CLK` rising edge, if the latch is set,
insert/repeat `Tw` instead of advancing to T3. Default `Wait = true`
(deasserted — the pin is active-low; expose it so an untouched pin never
stalls, like `Ready` defaults true on the 8080). The CP/M conformance harness
never touches it.

## Registers & flags

Own file, no reuse from `Intel8080/Registers.cs` (same technique, different
type):

- Main: `AF`, `BC`, `DE`, `HL` — union structs with `Value`/`Hi`/`Lo`
  (`A`/`F`, `B`/`C`, `D`/`E`, `H`/`L`).
- Index: `IX`, `IY` — need `IXH`/`IXL`/`IYH`/`IYL` byte halves for the
  undocumented DD/FD half-register opcodes.
- `SP`, `PC` — `Value`/`Hi`/`Lo`.
- `WZ` (a.k.a. MEMPTR) — internal, `Value`/`W`/`Z`. Required for `z80memptr`
  and the undocumented `SCF`/`CCF` flag behaviour; model it faithfully from
  the start rather than retrofitting.
- Alternate set: `AF'`, `BC'`, `DE'`, `HL'` — plain 16-bit; `EX AF,AF'` and
  `EXX` swap.
- `I` (interrupt vector), `R` (refresh — 7-bit auto-increment on every M1,
  bit 7 preserved, software-writable in full).
- `IFF1`, `IFF2` (bool), `IM` (0/1/2).

`Z80Flags` struct — bits **S Z Y H X P/V N C** (bit 7…0). `Y` (bit 5) and `X`
(bit 3) are the undocumented flags: for most ops they copy result bits 5/3;
for `BIT n,(HL)` / `SCF` / `CCF` / block ops they follow special rules (see
"The Undocumented Z80 Documented", ch. on flags). `P/V` is parity for
logical/rotate/`IN r,(C)` and overflow for arithmetic. Getting Y/X right is
what separates a ZEXDOC pass from a ZEXALL pass.

Target the **NMOS Zilog** Z80 behaviour (that is the die ZEXALL / z80test are
written against). Note the choice in the README; CMOS and clone (NEC, etc.)
`SCF`/`CCF` and `OUT (C),0` differences are out of scope.

## State machine

Parallel to the 8080's `MachineCycleType` + `State`:

```csharp
enum MachineCycleType : byte { OpcodeFetch, MemoryRead, MemoryWrite, IoRead, IoWrite, Internal, InterruptAck, /* NmiAck */ }
enum TState : byte { T1, T2, Tw, T3, T4, T5, T6 }   // Tw = wait, inserted between T2 and T3
```

`int` cycle key = `(mcType << 8) | tState`, with `const int OpcodeFetchT1 …`
labels. `_ir` holds the current opcode; a `_prefix` field (`None`/`CB`/`ED`/
`DD`/`FD`/`DDCB`/`FDCB`) selects which `Decode.*` partial's switch runs.
`_displacement` (sbyte) holds the `d` byte for `DD CB d`/`FD CB d`.

Prefix opcodes (`0xCB 0xED 0xDD 0xFD`) are handled as a fetch that sets
`_prefix` and immediately starts another `OpcodeFetch` (a real M1) rather than
decoding — `DD`/`FD` chains (`DD DD FD …`) just keep re-latching the prefix,
each its own 4-T M1, exactly like hardware.

Reset: `PC = 0`, `I = R = 0`, `IFF1 = IFF2 = false`, `IM = 0`, `SP = 0xFFFF`,
`AF = SP = 0xFFFF` on power-up (documented Zilog reset leaves AF/SP
undefined→0xFFFF in practice). `/RESET` held low for ≥3 T-states.

## Opcode implementation strategy

Hand-written microcode in the 8080 style, but **structured by the octal
`x`/`y`/`z`/`p`/`q` opcode split** from
<http://www.z80.info/decoding.htm>. That decode scheme collapses the
unprefixed and CB tables into a handful of families (e.g. all of
`LD r[y],r[z]` is one arm; all CB `rot[y] r[z]` is one arm), so the file
stays close to the 8080's size instead of 4× it, and the DD/FD partial is
mostly "run the base arm but substitute IX/IY and, for `(HL)`, splice in a
displacement fetch".

Instruction groups, roughly in build order:

1. **Core & 8-bit load** — `NOP`, `LD r,r'`, `LD r,n`, `LD r,(HL)`,
   `LD (HL),r`, `LD (HL),n`, `LD A,(BC/DE)`, `LD (BC/DE),A`, `LD A,(nn)`,
   `LD (nn),A`, `HALT` (0x76). `JP nn`. This nails the M1+refresh cycle and
   the read/write M-cycle edge map — verify against FUSE `00`, `40`, `70`,
   `3a`, `c3` before going wider.
2. **16-bit load / stack** — `LD dd,nn`, `LD SP,HL`, `LD (nn),HL`,
   `LD HL,(nn)`, `PUSH`/`POP` (note the extra internal T-state before the
   first stack write — 8080 `PUSH` shows the shape at `FetchT5`),
   `EX DE,HL`, `EX (SP),HL`, `EX AF,AF'`, `EXX`.
3. **8-bit ALU** — `ADD/ADC/SUB/SBC/AND/XOR/OR/CP` `A,r|n|(HL)`;
   `INC/DEC r|(HL)`; `DAA`, `CPL`, `NEG` (ED), `SCF`, `CCF`. Full S Z Y H X
   P/V N C. `Z80Chip.Alu.cs`.
4. **16-bit ALU** — `ADD HL,ss` (internal T-states, H/C from bit 11/15,
   sets Y/X/N; leaves S/Z/P/V), `INC/DEC ss`.
5. **Rotates & jumps** — `RLCA/RRCA/RLA/RRA`; `JR e`, `JR cc,e`, `DJNZ e`
   (internal T-state on B decrement); `CALL nn`, `CALL cc,nn`, `RET`,
   `RET cc`, `RST p`.
6. **I/O (base)** — `IN A,(n)`, `OUT (n),A` — the auto-Tw I/O cycle;
   WZ update rules.
7. **CB prefix** — `RLC/RRC/RL/RR/SLA/SRA/SLL/SRL r|(HL)`,
   `BIT/RES/SET b,r|(HL)`. `BIT` Y/X quirk (from address high byte for
   `(HL)`, from WZ high for `(IX+d)`).
8. **ED prefix** — `LD dd,(nn)` / `LD (nn),dd`; `ADC/SBC HL,ss` (internal
   T-states, full flags incl. overflow); `NEG`; `IM 0/1/2`;
   `LD A,I` / `LD A,R` (P/V ← IFF2, and the interrupt-glitch corner);
   `LD I,A` / `LD R,A`; `RRD` / `RLD`; `RETI` / `RETN`; block transfer
   `LDI/LDD/LDIR/LDDR`, block compare `CPI/CPD/CPIR/CPDR`, block I/O
   `INI/IND/INIR/INDR/OUTI/OUTD/OTIR/OTDR` (undocumented flag formulas —
   Undocumented-Z80 ch. 4/5).
9. **DD/FD prefix** — every op that references `HL`/`H`/`L` re-aimed at
   `IX`/`IXH`/`IXL` (resp. IY); `(HL)` → `(IX+d)` with the displacement
   fetched *and* an internal 5-T address-calc cycle spliced in; the
   undocumented `IXH`/`IXL` arithmetic and loads. `DD CB d op` / `FD CB d op`
   double-prefix: fetch `d`, fetch opcode, internal T-states, then the CB op
   on `(IX+d)` — and, for `op` with a register target field, the
   undocumented "write result to both `(IX+d)` and `r`" behaviour.
10. **Interrupts** — `/NMI` (edge-latched, `IFF1→IFF2` saved, jump to
    `0x0066`, 11 T-states, `RETN` restores `IFF1←IFF2`); `/INT` sampled at
    last T of instruction, blocked for one instruction after `EI`
    (`_eiShadow`); IM 1 (`RST 38h`, 13 T), IM 2 (`I:bus` vector table,
    19 T), IM 0 (execute opcode jammed on bus — minimal: assume `RST`).
    `HALT` executes NOP-M1s with `/HALT` low until an interrupt.
    `/BUSRQ` → `/BUSAK`, tristating the buses at the next M-cycle boundary.

## Reference implementations

Put these in `Z80/README.md`:

- **Zilog Z80 CPU User Manual** (UM0080) — pin timing diagrams; source of
  the edge map above.
- **"The Undocumented Z80 Documented"**, Sean Young — X/Y flags, MEMPTR/WZ,
  block-op and `IN`/`OUT` flag formulas, DD CB behaviour. Authoritative for
  everything ZEXALL/z80test check.
- **z80.info/decoding.htm** — the octal opcode-decode scheme the microcode is
  structured around.
- **floooh/chips `z80.h`** (Andre Weissflog, MIT) — a tick-accurate,
  pin-level Z80 in C; the single closest analogue to what we're building.
  Read for the per-tick pin sequencing.
- **superzazu/z80** (MIT) — compact instruction-level core that passes
  ZEXALL; good for cross-checking opcode *semantics* fast.
- **FUSE `z80.c` / `z80_ops.c`** (GPL — read for understanding, **do not
  copy**) — semantics + the origin of the `tests.in`/`tests.expected` data.
- **redcode/8080** and this repo's own `Intel8080Chip.cs` — the pin-API /
  staged-cycle pattern.

## Test ROMs & suites

All of these are long-standing, freely redistributable, and small enough to
commit alongside the existing `Intel8080/Assets/*.COM` files.

### 1. CP/M instruction exercisers — `Z80ChipTests.cs`

Same harness shape as `Intel8080ChipTests.Test8080`: load `.com` at `0x0100`,
`PC = 0x0100`, patch a trap at BDOS (`0x0005`) and warm-boot (`0x0000`),
run T-state by T-state servicing the bus off the control pins:

- read (`!MReq && !Rd`) → `Data = ram[Address]`
- write (`!MReq && !Wr`) → `ram[Address] = Data`
- `!IoRq && !Wr` on the trap port → BDOS emulation (function 2 = char in E,
  function 9 = `$`-terminated string at DE) / end-of-run signal

Trap style: mirror the 8080 test — patch `0x0005` with `OUT (n),A : RET` and
`0x0000` with `OUT (n),A`, detect completion / stream console output. (The
classic ZEX loader instead traps by watching `PC == 5`; either works,
patch-OUT keeps it identical to the existing 8080 harness.)

ROMs (`[Arguments]` rows):

| File | Source | Checks |
|---|---|---|
| `zexdoc.com` | Frank Cringle's exerciser, *documented*-flags variant | S Z H P/V N C only; every base + CB + ED + DD/FD group |
| `zexall.com` | same, *all*-flags variant | additionally Y/X undocumented flags |
| `z80doc.com` / `z80full.com` | Patrik Rak's `z80test` | tighter than ZEX; `z80full` = full incl. undocumented |
| `z80flags.com` | `z80test` | flag-only, fast smoke test |
| `z80ccf.com` | `z80test` | the `SCF`/`CCF` Y/X-from-A/-from-F corner |
| `z80memptr.com` | `z80test` | WZ/MEMPTR observable via `BIT n,(IX+d)` |

Each test prints `...OK` / `... ERROR` lines with CRCs; assert the output
contains no `ERROR` and the expected `OK` count. **Milestones:**
`z80flags` → `zexdoc` (after group 8) → `zexall` (Y/X correct) →
`z80full` + `z80ccf` + `z80memptr` (after groups 9-10).

Once outputs pass, add an **exact total-T-state-count assertion** per ROM
(the 8080 tests do this with `expectedCycleCount`). Capture the reference
counts from `floooh/chips` or FUSE and pin them — that assertion is the
half-cycle-timing ratchet for the architectural suite.

### 2. FUSE per-instruction bus-timing suite — `Z80ChipBusTimingTests.cs`

**This is the suite that actually proves half-cycle accuracy.** FUSE's
`tests.in` gives, per test: initial `AF BC DE HL AF' BC' DE' HL' IX IY SP PC`,
`I R IFF1 IFF2 IM halted`, `tstates`, and a list of `address value` memory
seeds. `tests.expected` gives the final register/pin state **plus a
timestamped list of every bus event** — `<time> MR <addr> <val>`,
`MW`, `MC` (no-op memory contention cycle), `PR`, `PW`, `PC` — i.e. which
half-T-state each `/MREQ`//`/IORQ` transition happens on.

Harness: for each of the ~1358 cases, construct `Z80Chip`, load the seed
state, run `tstates` T-states while recording `(halfCycleIndex, kind, addr,
val)` every time a control-pin combination goes active, then assert the
recorded trace equals the expected trace and the final registers match.
Ignore the ULA-contention (`MC`/`PC` timing-only) rows initially — assert
`MR`/`MW`/`PR`/`PW` address, value, **and edge index**. Skip-list any case
that depends on a genuinely undefined behaviour and note why.

Data files are plain text (~200 KB total) — commit them.

### 3. `Z80ChipWaitStateTests.cs`

Hand-written, mirrors `Intel8080ChipWaitStateTests`: assert `Wait` defaults
deasserted and never stalls; assert that holding `/WAIT` low across T2's
falling edge inserts `Tw`, repeats it while held, and resumes at T3 the
first sample after release; assert the address/data bus stay valid across
`Tw`; assert the I/O machine cycle always shows its one built-in `Tw` even
with `/WAIT` untouched.

### 4. `Z80ChipInterruptTests.cs`

Hand-written unit tests (no ROM covers these well): NMI edge latch + `0x0066`
vector + 11 T-states + `IFF1→IFF2` save + `RETN` restore; `EI` shadow (INT
not taken until after the following instruction); IM 1 timing (13 T), IM 2
vector fetch (19 T, `I:databus` address); `HALT` emits NOP M1s with `/HALT`
low and leaves HALT on the interrupt; `LD A,R` copies `IFF2` into P/V.

### 5. Optional / not committed — SingleStepTests (ex-TomHarte) `z80`

~1000 JSON cases per opcode with cycle-by-cycle `pins` strings. Huge
(hundreds of MB) — add a `[Explicit]`/opt-in test that reads from a
`git`-ignored local checkout path if present, for exhaustive spot-checking.
Not part of the default run.

## Disassembler (recommended, small)

`Debugging/Z80Disassembler.cs : Aemula.Debugging.Disassembler`, same shape as
`Intel8080Disassembler` (`DisassembleInstruction(ushort)` → `switch (opcode)`
with `Do0/Do1/Do2` helpers, `OnReset` seeding `0x0000`). Not strictly
required by "chip + tests", but it makes ZEX/FUSE failures readable ("wrong
flags after `SBC HL,DE` at 0x1A3F") instead of hex. Prefix tables
(`CB`/`ED`/`DD`/`FD`/`DDCB`) included. No UI / debugger window this pass
(no system to host it).

## What is explicitly out of scope

- Any `EmulatedSystem` (no ZX Spectrum, CP/M machine, arcade board, etc.).
- `UI/` companion (`CpuStateWindow`), logic-analyzer `ChannelGroup`,
  debugger wiring — deferred until a system needs them. (`CreateChannelGroup`
  / `CreateDebuggerWindows` can be stubbed following the 8080 for later.)
- Source-generated opcode tables (à la `Aemula.CodeGen/Mos6502CodeGenerator`).
  Viable future refactor once the hand-written version is green and its
  behaviour is the oracle; not now.
- CMOS Z80 / NEC clone / Z180 flag and `OUT (C),0` differences.
- Bus contention *timing values* — the FUSE test data was authored for the
  ZX Spectrum, whose ULA freezes the CPU clock for a few T-states whenever
  the CPU touches contended RAM or I/O while the ULA is drawing. FUSE encodes
  that as `MC`/`PC` ("memory/port contention") rows carrying only a
  timestamp. Contention is a *board-level* behaviour driven by the ULA, not
  anything the Z80 does on its own, so `Z80Chip` neither produces nor models
  it. The bus-timing harness parses those rows but asserts only on the real
  `MR`/`MW`/`PR`/`PW` bus events (address, value, half-cycle edge). Modelling
  contention belongs to a future `SpectrumSystem`, not this chip.

## Suggested phase / PR breakdown

| PR | Contents | Green when |
|---|---|---|
| 1 | Folder, `Z80Chip` skeleton: `Clk` setter + all pins + registers + flags + state machine + `SetNextCycle` staging. Reset. No opcodes. | builds; `Z80ChipWaitStateTests` (Tw mechanics, no opcodes needed) |
| 2 | Groups 1-2 (loads, stack, `JP`), M1+refresh edge map, memory M-cycles. `Z80Disassembler` base table. | FUSE cases for implemented opcodes; hand tests for M1 refresh timing |
| 3 | Groups 3-6 (8/16-bit ALU, rotates, jumps/calls, base I/O). | `z80flags.com`, `zexdoc.com` |
| 4 | Group 7 (CB) + group 8 (ED). | `zexall.com`, `z80doc.com` |
| 5 | Group 9 (DD/FD, DD CB/FD CB). | `z80full.com`, `z80ccf.com`, `z80memptr.com` |
| 6 | Group 10 (interrupts, HALT, BUSRQ). `Z80ChipInterruptTests`. | interrupt tests |
| 7 | Timing ratchet: exact T-state counts on the CP/M ROMs; full `Z80ChipBusTimingTests` (FUSE) enabled incl. edge-index assertions. | whole FUSE suite |
