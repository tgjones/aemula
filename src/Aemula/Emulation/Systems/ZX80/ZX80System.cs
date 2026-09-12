using System;
using System.IO;
using Aemula.Debugging;
using Aemula.Emulation.Chips;
using Aemula.Emulation.Chips.Z80;
using Aemula.Emulation.Systems.ZX80.Debugging;

namespace Aemula.Emulation.Systems.ZX80;

public sealed partial class ZX80System : EmulatedSystem
{
    // X1: a 6.5MHz ceramic resonator, the board's only clock source. Ticked
    // at 2x its own rate (13MHz) rather than 1x, because this class models
    // it edge-by-edge (see Tick()) - the Z80's own clock, the video shift
    // register and the horizontal counter chain are all synchronous
    // divisions of this same oscillator, and modelling at full-cycle
    // granularity would leave nowhere to hang the divide-by-2 flip-flop
    // that actually produces the CPU's 3.25MHz clock from it.
    public override ulong CyclesPerSecond => 13_000_000;

    public readonly Z80Chip Cpu;

    // IC2: the 4K mask ROM. A byte[] behind the chip that addresses it, per
    // the fidelity note that bulk storage stays plain data (matching
    // AppleISystem's DRAM banks) - only 12 address pins are bonded out on
    // the real part, so it mirrors every 4K within whatever range the
    // decode logic below selects it for.
    private readonly byte[] _rom = new byte[0x1000];

    // IC3/IC4: two 2114 1Kx4 SRAMs used together as 1Kx8 - the stock
    // machine's only RAM.
    private readonly byte[] _ram = new byte[0x400];

    // IC18, flip-flop A: Q wired back to its own D, the classic toggle-on-
    // every-rising-edge wiring that divides the master oscillator by 2 to
    // produce the Z80's own clock. The chip's other flip-flop is part of
    // the horizontal sync chain, wired up once video timing lands.
    private readonly Ttl7474Chip _clockDivider;

    private bool _masterClockHigh;

    // IC13: two of its six inverters produce NOT(/MREQ) (gate 1, pins 1->2)
    // and NOT(A14) (gate 6, pins 13->12) for the ROM/RAM select NAND below -
    // each net shared between both select gates rather than each having its
    // own MREQ or address inverter.
    private readonly Ttl7404Chip _addressDecodeInverters;

    // IC11: two of its four NANDs turn the two inverted signals above into
    // the ROM and RAM chip selects. NAND(NOT(A14), NOT(/MREQ)) (gate 4, pins
    // 12,13->11) is low exactly when A14 is low and /MREQ is asserted - the
    // ROM's own 16K block. NAND(A14, NOT(/MREQ)) (gate 3, pins 9,10->8) is
    // the complementary RAM decode. Neither gate looks at A15, which is why
    // the real board mirrors ROM into 8000-BFFF and RAM into C000-FFFF
    // rather than trapping those as open bus.
    private readonly Ttl7400Chip _romRamSelect;

    public ZX80System()
    {
        Cpu = new Z80Chip();
        _clockDivider = new Ttl7474Chip();
        _addressDecodeInverters = new Ttl7404Chip();
        _romRamSelect = new Ttl7400Chip();

        LoadRom();
    }

    private void LoadRom()
    {
        var romsDirectory = Path.Combine(AppContext.BaseDirectory, "Emulation", "Systems", "ZX80", "Roms");

        using var romStream = File.OpenRead(Path.Combine(romsDirectory, "zx80.rom"));
        romStream.ReadExactly(_rom);
    }

    public override void Tick()
    {
        // One master-oscillator edge (see CyclesPerSecond). IC18 flip-flop A
        // divides it by 2: D is tied to /Q, so it toggles on every rising
        // edge of the master clock, and its Q drives the Z80's own Clk pin
        // directly (the two 74LS05 buffer stages the real board adds here
        // are pure drive-strength, not logic - skipped, same as every other
        // plain buffer in this codebase).
        _masterClockHigh = !_masterClockHigh;

        _clockDivider.D1 = _clockDivider.Qn1;
        _clockDivider.Clk1 = _masterClockHigh;

        Cpu.Clk = _clockDivider.Q1;

        DoCpuMemoryAccess();
    }

    private void DoCpuMemoryAccess()
    {
        var address = Cpu.Address;

        _addressDecodeInverters.A1 = Cpu.MReq;
        _addressDecodeInverters.A6 = (address & 0x4000) != 0; // A14.

        var notMReq = _addressDecodeInverters.Y1;
        var notA14 = _addressDecodeInverters.Y6;

        _romRamSelect.A3 = (address & 0x4000) != 0; // A14.
        _romRamSelect.B3 = notMReq;
        var ramSelected = !_romRamSelect.Y3;

        _romRamSelect.A4 = notA14;
        _romRamSelect.B4 = notMReq;
        var romSelected = !_romRamSelect.Y4;

        if (romSelected && !Cpu.Rd)
        {
            Cpu.Data = _rom[address & 0x0FFF];
        }

        if (ramSelected)
        {
            if (!Cpu.Rd)
            {
                Cpu.Data = _ram[address & 0x03FF];
            }
            else if (!Cpu.Wr)
            {
                _ram[address & 0x03FF] = Cpu.Data;
            }
        }
    }

    internal byte ReadByteDebug(ushort address)
    {
        return (address & 0x4000) == 0
            ? _rom[address & 0x0FFF]
            : _ram[address & 0x03FF];
    }

    internal void WriteByteDebug(ushort address, byte value)
    {
        if ((address & 0x4000) != 0)
        {
            _ram[address & 0x03FF] = value;
        }
    }

    public override Debugger CreateDebugger()
    {
        return new ZX80Debugger(this);
    }
}
