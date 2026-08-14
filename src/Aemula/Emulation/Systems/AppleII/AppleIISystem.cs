using System;
using System.IO;
using Aemula.Debugging;
using Aemula.Emulation.Chips;
using Aemula.Emulation.Chips.Mos6502;
using Aemula.Emulation.Systems.AppleII.Debugging;

namespace Aemula.Emulation.Systems.AppleII;

public sealed class AppleIISystem : EmulatedSystem
{
    // The Apple II's master oscillator: 4x the NTSC color subcarrier (3.579545MHz).
    // The CPU clock, video dot clock, and color subcarrier are all synchronous
    // divisions of this one crystal, so everything ticks from it directly rather
    // than from a derived, coarser rate.
    public override ulong CyclesPerSecond => 14_318_180;

    public readonly Mos6502Chip Cpu;

    // 48K of RAM, mapped at $0000-$BFFF.
    private readonly byte[] _ram = new byte[0xC000];

    // Autostart Monitor + Applesoft BASIC, mapped at $D000-$FFFF.
    private readonly byte[] _rom = new byte[0x3000];

    // Character generator ROM (Signetics 2513 / Apple 341-0036).
    private readonly byte[] _characterRom = new byte[0x800];

    // Decodes the CPU address bus for everything from $C000 up: two enable
    // inverters feeding a single 3-to-8 decoder, addressed by A11-A13 and
    // qualified by A14/A15. This mirrors how the real board gets both the
    // I/O select and the six individual 2K ROM chip-selects (D0/D8/E0/E8/
    // F0/F8) out of one 74LS138, since $D000-$FFFF happens to be exactly
    // six 2K-aligned blocks. $0000-$BFFF (RAM) is simply "decoder disabled".
    private readonly Ttl7404Chip _addressDecodeInverters;
    private readonly Ttl74138Chip _highMemoryDecoder;

    // Placeholder divider bringing the 14.31818MHz master clock down to the
    // CPU's ~1.023MHz rate (7 master ticks low, 7 high). This isn't the real
    // hardware's non-uniform /13-with-one-stretched-cycle division - that
    // requires the video timing counter chain, which is phase 3's job. This
    // divider exists only so the CPU can run at all before then.
    private byte _cpuClockDivider;

    public AppleIISystem()
    {
        Cpu = new Mos6502Chip(Mos6502Options.Default);

        _addressDecodeInverters = new Ttl7404Chip();
        _highMemoryDecoder = new Ttl74138Chip();

        Cpu.Res = false;
        Cpu.Res = true;
    }

    public override void LoadProgram(string filePath)
    {
        var romsDirectory = Path.Combine(AppContext.BaseDirectory, "Emulation", "Systems", "AppleII", "Roms");

        using (var romStream = File.OpenRead(Path.Combine(romsDirectory, "Apple2_Plus.rom")))
        {
            romStream.ReadExactly(_rom);
        }

        using (var characterRomStream = File.OpenRead(Path.Combine(romsDirectory, "Apple2_Video.rom")))
        {
            characterRomStream.ReadExactly(_characterRom);
        }

        Reset();

        RaiseProgramLoaded();
    }

    public override void Reset()
    {
        Cpu.Res = false;
        Cpu.Res = true;
    }

    public override void Tick()
    {
        switch (_cpuClockDivider)
        {
            case 0:
                Cpu.Phi0 = false;
                break;

            case 7:
                Cpu.Phi0 = true;
                DoCpuMemoryAccess();
                break;
        }

        _cpuClockDivider++;
        if (_cpuClockDivider == 14)
        {
            _cpuClockDivider = 0;
        }
    }

    private void DoCpuMemoryAccess()
    {
        var address = Cpu.Address;

        SetHighMemoryDecoderAddress(address);

        if (Cpu.RW)
        {
            Cpu.Data = ReadByte(address);
        }
        else
        {
            WriteByte(address, Cpu.Data);
        }
    }

    private void SetHighMemoryDecoderAddress(ushort address)
    {
        _addressDecodeInverters.A1 = (address & 0x8000) != 0; // A15
        _addressDecodeInverters.A2 = (address & 0x4000) != 0; // A14

        _highMemoryDecoder.A = (address & 0x0800) != 0; // A11
        _highMemoryDecoder.B = (address & 0x1000) != 0; // A12
        _highMemoryDecoder.C = (address & 0x2000) != 0; // A13
        _highMemoryDecoder.G1 = true; // Tied high.
        _highMemoryDecoder.G2A = _addressDecodeInverters.Y1; // NOT(A15)
        _highMemoryDecoder.G2B = _addressDecodeInverters.Y2; // NOT(A14)
    }

    private byte ReadByte(ushort address)
    {
        if (!_highMemoryDecoder.Y0 || !_highMemoryDecoder.Y1)
        {
            // $C000-$CFFF: I/O space (soft switches, slot ROM). Not wired up
            // yet, so it reads as open bus.
            return 0xFF;
        }

        if (!_highMemoryDecoder.Y2 || !_highMemoryDecoder.Y3 || !_highMemoryDecoder.Y4 ||
            !_highMemoryDecoder.Y5 || !_highMemoryDecoder.Y6 || !_highMemoryDecoder.Y7)
        {
            // $D000-$FFFF: one of the six 2K ROM ICs (D0/D8/E0/E8/F0/F8),
            // pre-joined into a single flat image.
            return _rom[address - 0xD000];
        }

        return _ram[address];
    }

    private void WriteByte(ushort address, byte value)
    {
        if (!_highMemoryDecoder.Y0 || !_highMemoryDecoder.Y1)
        {
            // I/O space - not wired up yet.
            return;
        }

        if (!_highMemoryDecoder.Y2 || !_highMemoryDecoder.Y3 || !_highMemoryDecoder.Y4 ||
            !_highMemoryDecoder.Y5 || !_highMemoryDecoder.Y6 || !_highMemoryDecoder.Y7)
        {
            // Can't write to ROM.
            return;
        }

        _ram[address] = value;
    }

    internal byte ReadByteDebug(ushort address)
    {
        SetHighMemoryDecoderAddress(address);

        return ReadByte(address);
    }

    internal void WriteByteDebug(ushort address, byte value)
    {
        SetHighMemoryDecoderAddress(address);

        WriteByte(address, value);
    }

    public override Debugger CreateDebugger()
    {
        return new AppleIIDebugger(this);
    }
}
