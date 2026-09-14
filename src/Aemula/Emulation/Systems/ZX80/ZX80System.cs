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

    // A behavioural (not netlist-traced) stand-in for the real board's
    // address decode: tracing the phase 3 netlist in full turned up that the
    // real ROM/RAM chip-select network doesn't run through IC11 or IC13 at
    // all (IC11's four gates are fully spoken for by the video-polarity and
    // horizontal-counter-reset latches in ZX80System.Video.cs, and IC13's
    // gate 6 inverts a signal that isn't A14). This pair produces the same
    // ROM-vs-RAM boolean the real decode does for every address that
    // matters, so it's kept rather than reworked mid-plan, but it isn't
    // claiming to be any specific pair of real gates any more.
    private readonly Ttl7404Chip _addressDecodeInverters;
    private readonly Ttl7400Chip _romRamSelect;

    // IC18: two roles on the real board, both modelled on this one
    // instance. Flip-flop 2 (D2 tied to its own Qn2) divides PHI2X by 2 to
    // produce the Z80's own clock, PHI. Flip-flop 1 has no clock of its own
    // (CLK1 is tied to +5V) - it's a pure async latch, part of the
    // horizontal sync chain wired up in ZX80System.Video.cs.
    private readonly Ttl7474Chip _ic18;

    // IC20: gate 1 buffers the abstracted master-oscillator toggle below
    // into PHI2X; gate 2 will XOR the video shift register's output against
    // the inverse-video latch to produce the composite VIDEO bit, once
    // phase 4 needs it. Gates 3-4 form the crystal's own Pierce-oscillator
    // feedback loop - out of scope, like the RF modulator (see the plan's
    // fidelity notes): X1's oscillation itself is the abstracted toggle
    // this gate buffers, the same "stop at the analog boundary" convention
    // as every other exception in this codebase.
    private readonly Ttl7486Chip _ic20;

    private bool _masterOscillatorHigh;

    // The ROM/RAM-side half of the split data bus (D0'-D7' on the
    // schematic) - a genuinely separate value from Cpu.Data (the CPU-side
    // half D0-D7), joined to it only through the per-bit resistor/gate
    // combine in CombineSplitDataBus. Sampled in DoCpuMemoryAccess before
    // the NOP-forcing bank runs, since that bank only ever acts on the
    // CPU-side half.
    private byte _farDataBus;

    public ZX80System()
    {
        Cpu = new Z80Chip();
        _addressDecodeInverters = new Ttl7404Chip();
        _romRamSelect = new Ttl7400Chip();
        _ic18 = new Ttl7474Chip();
        _ic20 = new Ttl7486Chip();

        _characterLatch = new Ttl74373Chip();
        _romAddressMuxHigh = new Ttl74157Chip();
        _romAddressMuxMid = new Ttl74157Chip();
        _romAddressMuxLow = new Ttl74157Chip();
        _videoShiftRegister = new Ttl74165Chip();
        _scanlineCounter = new Ttl7493Chip();
        _ic11 = new Ttl7400Chip();
        _ic12 = new Ttl7400Chip();
        _ic13 = new Ttl7404Chip();
        _ic14 = new Ttl7405Chip();
        _ic15 = new Ttl7405Chip();
        _ic16 = new Ttl7410Chip();
        _ic17 = new Ttl7432Chip();
        _ic19 = new Ttl7474Chip();

        _cassetteBuffer = new Ttl74365Chip();
        PeripheralRequests = BuildCassettePeripheralRequests();

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
        // One master-oscillator edge (see CyclesPerSecond). X1 itself, and
        // the Pierce-oscillator feedback pair IC20 builds around it, are the
        // analog part of the crystal circuit - out of scope the same way the
        // RF modulator is (see the plan's fidelity notes); this toggle is
        // the abstracted result, feeding the real IC20 gate that buffers it
        // into PHI2X.
        _masterOscillatorHigh = !_masterOscillatorHigh;

        _ic20.A1 = false;
        _ic20.B1 = _masterOscillatorHigh;
        var phi2X = _ic20.Y1;

        // IC18, flip-flop 2: D tied to its own Qn, the classic toggle-on-
        // every-rising-edge wiring that divides PHI2X by 2 to produce the
        // Z80's own clock, PHI. The chip's other flip-flop has no clock of
        // its own (CLK1 is tied to +5V) and is part of the horizontal sync
        // chain instead - see ZX80System.Video.cs.
        _ic18.D2 = _ic18.Qn2;
        _ic18.Clk2 = phi2X;

        Cpu.Clk = _ic18.Q2;

        // Before DoCpuMemoryAccess: EAR's buffered level has to be settled
        // on _cassetteBuffer.Y1 before ReadKeyboardMatrix (called from
        // within it) reads D7 back off that same buffer.
        TickCassette();
        DoCpuMemoryAccess();
        TickVideo(phi2X);
        TickCompositeVideo();
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

        // Unlike the RAM below, this isn't gated on Cpu.Rd: a real mask ROM
        // has no read/write pin at all, so its outputs are enabled purely by
        // chip-select (romSelected, itself already gated on /MREQ) - and the
        // Z80 never asserts /RD during the refresh half of an M1 cycle,
        // which is exactly when this same ROM access has to fire to put the
        // character bitmap byte on the bus for the video shift register.
        // Gating on Rd here silently broke that: the CPU-side (T1/T2) fetch
        // still worked since /RD is asserted there, but the refresh-side
        // (T3/T4) bitmap fetch this whole video trick depends on never ran.
        if (romSelected)
        {
            Cpu.Data = _rom[GetRomAddress(address)];
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

        // IC5's own data inputs (D0'-D7') are wired to the ROM/RAM side of
        // the split bus, not the CPU-visible side IC14/IC15 are about to
        // force below - on real hardware the two sides only agree everywhere
        // except the exact bits the NOP generator pulls down, which is what
        // lets IC5 latch the true character code while the Z80 sees a NOP.
        // Captured here, before the NOP-forcing bank runs, since that bank
        // only ever pulls the CPU-side half (Cpu.Data) down - the far side
        // it reads from is this one, never the forced result.
        _farDataBus = Cpu.Data;

        // The NOP generator: during the T1 (non-refresh) half of an opcode
        // fetch, IC14/IC15 force the whole data byte to 0x00 unless the CPU
        // is halted, address bit 15 is clear, or bit 6 of the real byte just
        // placed on the bus above is set - see TickNopGenerator for why.
        if (Cpu.Rfsh && !Cpu.M1)
        {
            TickNopGenerator();
        }

        // The keyboard read: GetKbdSignal is already false exactly when this
        // is an I/O read of an even port (see its own remarks) - which on
        // this board only ever means the keyboard, there being no other I/O
        // device decoded on A0 alone.
        if (!GetKbdSignal())
        {
            Cpu.Data = ReadKeyboardMatrix(Cpu.Address);
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
