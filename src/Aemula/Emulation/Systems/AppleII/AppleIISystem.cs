using System;
using System.IO;
using Aemula.Debugging;
using Aemula.Emulation.Chips;
using Aemula.Emulation.Chips.Mos6502;
using Aemula.Emulation.Systems.AppleII.Debugging;
using Aemula.UI.Oscilloscope;

namespace Aemula.Emulation.Systems.AppleII;

public sealed partial class AppleIISystem : EmulatedSystem
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

    // Decodes the CPU address bus for everything from $C000 up. Four
    // chained 74LS138s plus a hex inverter (Sather ch. 7, "Address Decoding
    // and Input/Output", Figures 7.1-7.3) - all four of the plan's budgeted
    // 74LS138s are used here:
    //
    //   F12 (_highMemoryDecoder): A11-A13, qualified by A14/A15, into eight
    //   2K sections: Y0 = the I/O Section ($C000-$C7FF), Y1 = the "seventh
    //   ROM" / I/O STROBE' range ($C800-$CFFF, peripheral expansion ROM),
    //   Y2-Y7 = the six 2K ROM chips (D0/D8/E0/E8/F0/F8, $D000-$FFFF).
    //   $0000-$BFFF (RAM) is simply "decoder disabled".
    //
    //   H12 (_ioSectionDecoder): further divides F12's Y0 ($C000-$C7FF, via
    //   A8-A10) into eight 256-byte blocks: Y0 = $C000-$C0FF (motherboard
    //   control, divided further below), Y1-Y7 = the seven slots'
    //   $C1XX-$C7XX I/O SELECT' ranges (not wired up - no slot cards yet).
    //
    //   H12's Y0 is itself split by A7/A7' (the third inverter gate on
    //   _addressDecodeInverters) into two 128-byte halves:
    //
    //   H2 (_deviceSelectDecoder): the A7 (high) half, $C080-$C0FF, into
    //   eight 16-byte DEVICE SELECT' ranges (Table 7.1) - not wired up.
    //
    //   F13 (_ioControlDecoder): the A7' (low) half, $C000-$C07F, into
    //   eight 16-byte motherboard control ranges - keyboard data ($C00X),
    //   clear strobe ($C01X), cassette out ($C02X), speaker ($C03X), C040
    //   STROBE' ($C04X), screen mode/annunciator switches ($C05X), serial
    //   input mux ($C06X), paddle trigger ($C07X).
    private readonly Ttl7404Chip _addressDecodeInverters;
    private readonly Ttl74138Chip _highMemoryDecoder;
    private readonly Ttl74138Chip _ioSectionDecoder;
    private readonly Ttl74138Chip _deviceSelectDecoder;
    private readonly Ttl74138Chip _ioControlDecoder;

    public AppleIISystem()
    {
        Cpu = new Mos6502Chip(Mos6502Options.Default);

        _addressDecodeInverters = new Ttl7404Chip();
        _highMemoryDecoder = new Ttl74138Chip();
        _ioSectionDecoder = new Ttl74138Chip();
        _deviceSelectDecoder = new Ttl74138Chip();
        _ioControlDecoder = new Ttl74138Chip();

        _videoScannerD14 = new Ttl74161Chip();
        _videoScannerD13 = new Ttl74161Chip();
        _videoScannerD12 = new Ttl74161Chip();
        _videoScannerD11 = new Ttl74161Chip();

        _clockSequencer = new Ttl74S195Chip();

        _phase0FlipFlops = new Ttl74175Chip();
        _phase0Mux = new Ttl74153Chip();

        _videoAddressAdder = new Ttl74283Chip();
        _textVideoShiftRegister = new Ttl74166Chip();
        _textVideoXor = new Ttl7486Chip();
        _invertTextLatch = new Ttl7474Chip();

        _modeSwitchLatch = new Ttl74259Chip();
        _hiresVideoShiftRegister = new Ttl74166Chip();

        Display = new DisplayBuffer(280, 192);
        HiresColorPhase = new byte[280 * 192];

        _keyboardEncoder = new Ay53600Chip();
        _keyboardStrobeLatch = new Ttl7474Chip();

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
        TickVideoTiming();
        TickKeyboard();
    }

    private void DoCpuMemoryAccess()
    {
        var address = Cpu.Address;

        SetHighMemoryDecoderAddress(address);
        SetIoControlDecoderAddress(address);
        SetModeSwitchLatchAddress(address);

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

    private void SetIoControlDecoderAddress(ushort address)
    {
        // H12: eight 256-byte blocks of the I/O Section (F12's Y0).
        _ioSectionDecoder.A = (address & 0x100) != 0; // A8
        _ioSectionDecoder.B = (address & 0x200) != 0; // A9
        _ioSectionDecoder.C = (address & 0x400) != 0; // A10
        _ioSectionDecoder.G1 = true; // Tied high.
        _ioSectionDecoder.G2A = _highMemoryDecoder.Y0;
        _ioSectionDecoder.G2B = false; // Tied low.

        _addressDecodeInverters.A3 = (address & 0x80) != 0; // A7

        // H2: the A7 (high) half of H12's Y0, $C080-$C0FF - eight 16-byte
        // DEVICE SELECT' ranges.
        _deviceSelectDecoder.A = (address & 0x10) != 0; // A4
        _deviceSelectDecoder.B = (address & 0x20) != 0; // A5
        _deviceSelectDecoder.C = (address & 0x40) != 0; // A6
        _deviceSelectDecoder.G1 = true; // Tied high.
        _deviceSelectDecoder.G2A = _ioSectionDecoder.Y0;
        _deviceSelectDecoder.G2B = _addressDecodeInverters.Y3; // NOT(A7)

        // F13: the A7' (low) half of H12's Y0, $C000-$C07F - eight 16-byte
        // motherboard control ranges.
        _ioControlDecoder.A = (address & 0x10) != 0; // A4
        _ioControlDecoder.B = (address & 0x20) != 0; // A5
        _ioControlDecoder.C = (address & 0x40) != 0; // A6
        _ioControlDecoder.G1 = true; // Tied high.
        _ioControlDecoder.G2A = _ioSectionDecoder.Y0;
        _ioControlDecoder.G2B = (address & 0x80) != 0; // A7 directly (asserted when A7=0)
    }

    private byte ReadByte(ushort address)
    {
        if (!_highMemoryDecoder.Y0)
        {
            // The I/O Section ($C000-$C7FF; Y1, $C800-$CFFF, is the
            // separate "seventh ROM"/I-O-STROBE' range, handled below).
            if (!_ioControlDecoder.Y0)
            {
                // Keyboard data: bits 0-6 from the AY-5-3600, bit 7 the
                // latched strobe flag.
                return ReadKeyboardData();
            }

            if (!_ioControlDecoder.Y1)
            {
                // Clear keyboard strobe. Any of these 16 addresses does the
                // same thing (Table 7-6).
                ClearKeyboardStrobe();
                return 0xFF;
            }

            if (!_ioControlDecoder.Y5)
            {
                // Screen mode soft switches (and, at $C058-$C05F, the
                // annunciators - latched here too since it's the same
                // physical 74LS259, but not consumed until a later phase).
                // SetModeSwitchLatchAddress already updated the latch for
                // this access, regardless of read or write.
                return 0xFF;
            }

            // Remaining I/O Section (cassette, speaker, C040 strobe, serial
            // mux, paddle trigger, slot I/O SELECT'/DEVICE SELECT') not
            // wired up yet, so it reads as open bus.
            return 0xFF;
        }

        if (!_highMemoryDecoder.Y1)
        {
            // $C800-$CFFF: peripheral card expansion ROM. No slot cards
            // implemented, so open bus.
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
        if (!_highMemoryDecoder.Y0)
        {
            if (!_ioControlDecoder.Y1)
            {
                ClearKeyboardStrobe();
            }

            // $C05X (mode switches) and the rest of the I/O Section - not
            // wired up for writes beyond what SetModeSwitchLatchAddress
            // already did for this access.
            return;
        }

        if (!_highMemoryDecoder.Y1)
        {
            // $C800-$CFFF: can't write to expansion ROM (no slot cards).
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
        SetIoControlDecoderAddress(address);
        SetModeSwitchLatchAddress(address);

        return ReadByte(address);
    }

    internal void WriteByteDebug(ushort address, byte value)
    {
        SetHighMemoryDecoderAddress(address);
        SetIoControlDecoderAddress(address);
        SetModeSwitchLatchAddress(address);

        WriteByte(address, value);
    }

    public override Debugger CreateDebugger()
    {
        return new AppleIIDebugger(this);
    }

    internal ScopeChannelGroup CreateScopeChannelGroup()
    {
        return new ScopeChannelGroup("Apple II",
        [
            Cpu.CreateScopeChannelGroup(),
            new ScopeChannelGroup("Video Timing",
            [
                ScopeChannel.Digital("HBL", () => Hbl),
                ScopeChannel.Digital("VBL", () => Vbl),
                ScopeChannel.Digital("Color Burst Gate", () => ColorBurstGate),
                ScopeChannel.Digital("Phase 0", () => Phase0),
                ScopeChannel.Digital("HSync", () => HSyncPulse),
                ScopeChannel.Digital("VSync", () => VSyncPulse),
                ScopeChannel.Digital("Video Data", () => VideoDataBit),
                ScopeChannel.Analog("Composite Video", () => CurrentCompositeVideoSample, 0, 255,
                [
                    (0, "Sync"),
                    (64, "Black"),
                    (255, "White"),
                ]),
            ]),
        ]);
    }
}
