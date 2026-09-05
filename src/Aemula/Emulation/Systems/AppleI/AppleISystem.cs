using Aemula.Debugging;
using Aemula.Emulation.Chips;
using Aemula.Emulation.Chips.Mos6502;
using Aemula.Emulation.Chips.Mos6820;
using Aemula.Emulation.Systems.AppleI.Debugging;
using Aemula.Emulation.Systems.AppleI.Roms;

namespace Aemula.Emulation.Systems.AppleI;

// The PIA's display-side handshake lines (CB2, and the keyboard side
// PA/CA1) are left at their power-on levels - no character memory or
// keyboard wiring yet.
public sealed partial class AppleISystem : EmulatedSystem
{
    // The board's master oscillator: 4x the NTSC colour subcarrier
    // (3.579545MHz), the same crystal AppleIISystem ticks at (ZQ1 on the
    // schematic, 14.31818MHz). The CPU clock (1.022727MHz, exactly 2/7 of
    // the subcarrier) and the video dot clock are both synchronous
    // divisions of this one oscillator - see the plan's "Composite video"
    // section.
    public override ulong CyclesPerSecond => 14_318_180;

    public readonly Mos6502Chip Cpu;

    // 8K RAM (both onboard MK4096 banks populated), mapped at $0000-$1FFF -
    // see the plan's chip inventory (ICA11-18/ICB11-18). Both banks share
    // one M0-M7 data bus on real hardware (safe since their chip-selects,
    // CS0/CS1 below, are mutually exclusive), so one flat array already
    // matches the observable behaviour; the row/column-multiplexing 74157s
    // (ICB5-ICB8) and the RAS'/CAS' pulses they generate only matter once
    // DRAM refresh rides on the video scan, so they're not modelled as
    // separate chip instances here - same reasoning AppleIISystem uses for
    // its own DRAM (it doesn't model equivalent bus-buffer/mux chips
    // either).
    private readonly byte[] _ram = new byte[0x2000];

    // The Monitor ROM (WozMon). ICA1/ICA2 only have 8 address pins (A0-A7),
    // so on real hardware the 256-byte image mirrors every page of the CSF
    // block below - $FF00-$FFFF (where the 6502's vectors live) is just the
    // top instance of that mirroring, not a separately decoded range.
    private readonly byte[] _rom = WozMonitor.Image;

    public readonly Mos6820Chip Pia;

    // ICB9: the chip-select generator - a single 4-to-16 decoder over
    // A12-A15 dividing the whole 64K map into 4K blocks (Y0-Y15 = CS0-CSF).
    // Y0/Y1 (jumpers X/W) select the two RAM banks, Y13 (jumper Z) the PIA,
    // Y15 (jumper Y) the ROM; the rest are unpopulated expansion blocks.
    private readonly Ttl74154Chip _chipSelectDecoder;

    public AppleISystem()
    {
        Cpu = new Mos6502Chip(Mos6502Options.Default);
        Pia = new Mos6820Chip();
        _chipSelectDecoder = new Ttl74154Chip();

        _horizontalCounterLow = new Ttl74160Chip();
        _horizontalCounterHigh = new Ttl74161Chip();
        _characterAddressLow = new Ttl74161Chip();
        _characterAddressHigh = new Ttl74161Chip();
        _verticalCounterLow = new Ttl74161Chip();
        _verticalCounterHigh = new Ttl74161Chip();

        Cpu.Res = false;
        Cpu.Res = true;

        Pia.Res = false;
        Pia.Res = true;

        ResetCharacterMemory();
    }

    public override void LoadProgram(string filePath)
    {
        // No cassette support yet (see the plan's "Target configuration" -
        // out of scope until the cassette-interface stretch goal), and the
        // Monitor ROM is fixed, so there's nothing to load from filePath yet.
        Reset();

        RaiseProgramLoaded();
    }

    public override void Reset()
    {
        Cpu.Res = false;
        Cpu.Res = true;

        Pia.Res = false;
        Pia.Res = true;

        ResetCharacterMemory();
    }

    public override void Tick()
    {
        // Drives Cpu.Phi0 off the real horizontal/vertical counter chain and,
        // on its rising edge, calls DoCpuMemoryAccess() - see
        // AppleISystem.VideoTiming.cs.
        TickVideoTiming();
    }

    private void DoCpuMemoryAccess()
    {
        var address = Cpu.Address;

        SetChipSelectDecoderAddress(address);

        // Cs0/Cs2 aren't traceable past the 74154 on the rendered schematic
        // tiles, so they're assumed tied to their inactive-safe levels
        // (high/low) and CSD (Cs1) alone gates selection.
        var piaSelected = !_chipSelectDecoder.Y13;

        Pia.Rs0 = (address & 0x01) != 0;
        Pia.Rs1 = (address & 0x02) != 0;
        Pia.RW = Cpu.RW;
        Pia.Cs0 = true;
        Pia.Cs1 = piaSelected;
        Pia.Cs2 = false;

        if (piaSelected)
        {
            // DB is only driven from the shared bus - and only pulsed -
            // while actually selected, so it's otherwise left holding
            // whatever the PIA last really answered (what ReadByteDebug/
            // WriteByteDebug read back later, without re-triggering a live
            // access, rather than whatever unrelated ROM/RAM byte happened
            // to cross the bus most recently).
            Pia.DB = Cpu.Data;

            // One full E pulse per bus cycle - not yet the schematic's real
            // continuously-running E (that only matters once CA2/CB2
            // handshake timing does), but enough to commit exactly one
            // register access: the rising edge commits a read, the falling
            // edge commits a write.
            Pia.E = false;
            Pia.E = true;
            Pia.E = false;
        }

        if (Cpu.RW)
        {
            Cpu.Data = ReadByte(address);
        }
        else
        {
            WriteByte(address, Cpu.Data);
        }
    }

    private void SetChipSelectDecoderAddress(ushort address)
    {
        _chipSelectDecoder.A = (address & 0x1000) != 0; // A12
        _chipSelectDecoder.B = (address & 0x2000) != 0; // A13
        _chipSelectDecoder.C = (address & 0x4000) != 0; // A14
        _chipSelectDecoder.D = (address & 0x8000) != 0; // A15
        _chipSelectDecoder.G1 = false; // Tied low - always enabled.
        _chipSelectDecoder.G2 = false; // Tied low - always enabled.
    }

    private byte ReadByte(ushort address)
    {
        if (!_chipSelectDecoder.Y0 || !_chipSelectDecoder.Y1)
        {
            return _ram[address];
        }

        if (!_chipSelectDecoder.Y13)
        {
            // The PIA's own chip-select input beyond RS0/RS1 is just this
            // one 4K block strobe, so it responds identically at every
            // 4-byte-aligned offset in $D000-$DFFF - $D010-$D013
            // (KBD/KBDCR/DSP/DSPCR) is simply the instance of that
            // mirroring the Monitor ROM actually uses.
            return Pia.DB;
        }

        if (!_chipSelectDecoder.Y15)
        {
            return _rom[address & 0xFF];
        }

        return 0xFF;
    }

    private void WriteByte(ushort address, byte value)
    {
        if (!_chipSelectDecoder.Y0 || !_chipSelectDecoder.Y1)
        {
            _ram[address] = value;
        }

        // A PIA write already happened above, as a side effect of Pia.E
        // falling with Cs1/DB set for this address - nothing further to do
        // here. The ROM can't be written.
    }

    internal byte ReadByteDebug(ushort address)
    {
        SetChipSelectDecoderAddress(address);

        return ReadByte(address);
    }

    internal void WriteByteDebug(ushort address, byte value)
    {
        SetChipSelectDecoderAddress(address);

        WriteByte(address, value);
    }

    public override Debugger CreateDebugger()
    {
        return new AppleIDebugger(this);
    }
}
