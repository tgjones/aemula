using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Aemula.Emulation.Chips.Ricoh2C02.UI;
using Aemula.UI;

namespace Aemula.Emulation.Chips.Ricoh2C02;

// https://wiki.nesdev.com/w/index.php/PPU_registers
public sealed partial class Ricoh2C02Chip
{
    private const byte PpuCtrlAddress = 0x0;
    private const byte PpuMaskAddress = 0x1;
    private const byte PpuStatusAddress = 0x2;
    private const byte OamAddrAddress = 0x3;
    private const byte OamDataAddress = 0x4;
    private const byte PpuScrollAddress = 0x5;
    private const byte PpuAddrAddress = 0x6;
    private const byte PpuDataAddress = 0x7;

    private readonly byte[] _objectAttributeMemory;
    private byte _oamAddress;

    private readonly Color[] _systemPalette;
    private readonly byte[] _paletteMemory;

    // NESDev "loopy" PPU registers. See https://www.nesdev.org/wiki/PPU_scrolling
    internal ushort _v; // Current VRAM address (15 bits)
    internal ushort _t; // Temporary VRAM address (15 bits)
    internal byte _x;   // Fine X scroll (3 bits)
    internal bool _w;   // Write toggle: false = first write, true = second write

    private byte _ppuReadBuffer;
    private VramReadTarget _vramReadTarget;

    // Set in response to the CPU reading from or writing to VRAM.
    // Used in the next PPU cycle to trigger the actual reads or writes.
    private VramRequestState _vramRequestState;
    private ushort _vramRequestAddress;
    private byte _vramRequestData;

    // PPU open-bus "decay register" (ppu_open_bus test). Eight independent bits,
    // each driven on any register write (all 8) or on a register read that
    // defines it (a per-register mask). A bit last driven to 1 more than
    // OpenBusDecayCycles PPU cycles ago has decayed back to 0. Reads of
    // write-only registers and of unimplemented bits return whatever is left.
    private byte _openBus;
    private readonly ulong[] _openBusSetCycle = new ulong[8];

    // ~600 ms at the 5.37 MHz dot clock: inside the ppu_open_bus "decayed within
    // one second" window and far past every refresh-then-check sequence.
    private const ulong OpenBusDecayCycles = 3_221_591;

    // Registers
    internal PpuCtrlRegister CtrlRegister;
    internal PpuMaskRegister MaskRegister;
    internal PpuStatusRegister StatusRegister;

    internal ulong Cycles;
    internal ulong Frames;
    internal ulong CurrentScanline;
    internal ulong CurrentDot;

    // Master-clock input state and its divide-by-four counter. The system drives
    // Clk low/high once per master period (2 transitions); the counter tracks
    // those half-periods and runs one dot (CycleDot) every 8 of them. It starts
    // at 7 so the very first genuine transition after construction completes a
    // dot - the same phase the old Tick() had, where _dotClockDivider started at
    // 0 and ran a dot on the first call.
    private bool _clk;
    private int _clkDivideCounter = 7;

    // Chip select / data-bus enable (D̄B̄Ē), active low. Idle high; a falling edge
    // performs one CPU-register access (see OnDbeActive).
    private bool _dbe = true;

    // Pin values. Every hardware pin is a property whose direction matches the
    // real chip. Inputs are set-only, outputs get-only, the two buses both.

    private bool _cpuRw;
    private byte _cpuAddress;
    private byte _cpuData;

    /// <summary>
    /// To save pins the PPU multiplexes the low eight VRAM address pins, also
    /// using them as the VRAM data pins. The overlap is modelled by
    /// <see cref="MultiplexedAddressData"/>; only <see cref="PpuAddressBus"/> and
    /// <see cref="PpuData"/> are exposed.
    /// </summary>
    private MultiplexedAddressData _adBus;

    private bool _ppuAle;
    private bool _ppuRd;
    private bool _ppuWr;
    private bool _nmi;

    /// <summary>R/W̄ - CPU-bus read/write select (input).</summary>
    public bool CpuRw
    {
        set => _cpuRw = value;
    }

    /// <summary>RS0-RS2 - CPU-bus register select (input).</summary>
    public byte CpuAddress
    {
        set => _cpuAddress = value;
    }

    /// <summary>D0-D7 - CPU data bus (bidirectional).</summary>
    public byte CpuData
    {
        get => _cpuData;
        set => _cpuData = value;
    }

    /// <summary>
    /// AD0-AD7 / A8-A13 - the 14-bit VRAM address the PPU is driving (output).
    /// </summary>
    public ushort PpuAddressBus => _adBus.Address;

    /// <summary>
    /// AD0-AD7 - the multiplexed low-byte data bus: the byte the PPU drives on a
    /// write, or where a read byte is delivered back (bidirectional).
    /// </summary>
    public byte PpuData
    {
        get => _adBus.Data;
        set => _adBus.Data = value;
    }

    /// <summary>ALE - address latch enable (output).</summary>
    public bool PpuAle => _ppuAle;

    /// <summary>R̄D̄ - VRAM read strobe, active low (output).</summary>
    public bool PpuRd => _ppuRd;

    /// <summary>W̄R̄ - VRAM write strobe, active low (output).</summary>
    public bool PpuWr => _ppuWr;

    /// <summary>I̅N̅T̅ - connected to the CPU's N̅M̅I̅ pin, active low (output).</summary>
    public bool Nmi => _nmi;

    /// <summary>
    /// D̄B̄Ē - data-bus enable / chip select, active low (input). Idle high; the
    /// system pulses it low then high once per CPU access to the PPU's $2000-$2007
    /// ports, and the falling edge runs that register read/write - the same shape
    /// as the 2A03 running its bus service off an M2 edge.
    /// </summary>
    public bool Dbe
    {
        set
        {
            if (_dbe == value)
            {
                return;
            }

            _dbe = value;

            if (!value)
            {
                OnDbeActive();
            }
        }
    }

    /// <summary>
    /// The AD0-AD7 multiplex: the low VRAM address byte and the VRAM data byte
    /// share pins, so <see cref="Data"/> overlays the low byte of
    /// <see cref="Address"/>.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct MultiplexedAddressData
    {
        [FieldOffset(0)]
        public ushort Address;

        [FieldOffset(1)]
        public byte AddressHi;

        [FieldOffset(0)]
        public byte Data;
    }

    public Ricoh2C02Chip()
    {
        _objectAttributeMemory = new byte[256];

        _systemPalette =
        [
            new Color(84, 84, 84),
            new Color(0, 30, 116),
            new Color(8, 16, 144),
            new Color(48, 0, 136),
            new Color(68, 0, 100),
            new Color(92, 0, 48),
            new Color(84, 4, 0),
            new Color(60, 24, 0),
            new Color(32, 42, 0),
            new Color(8, 58, 0),
            new Color(0, 64, 0),
            new Color(0, 60, 0),
            new Color(0, 50, 60),
            new Color(0, 0, 0),
            new Color(0, 0, 0),
            new Color(0, 0, 0),

            new Color(152, 150, 152),
            new Color(8, 76, 196),
            new Color(48, 50, 236),
            new Color(92, 30, 228),
            new Color(136, 20, 176),
            new Color(160, 20, 100),
            new Color(152, 34, 32),
            new Color(120, 60, 0),
            new Color(84, 90, 0),
            new Color(40, 114, 0),
            new Color(8, 124, 0),
            new Color(0, 118, 40),
            new Color(0, 102, 120),
            new Color(0, 0, 0),
            new Color(0, 0, 0),
            new Color(0, 0, 0),

            new Color(236, 238, 236),
            new Color(76, 154, 236),
            new Color(120, 124, 236),
            new Color(176, 98, 236),
            new Color(228, 84, 236),
            new Color(236, 88, 180),
            new Color(236, 106, 100),
            new Color(212, 136, 32),
            new Color(160, 170, 0),
            new Color(116, 196, 0),
            new Color(76, 208, 32),
            new Color(56, 204, 108),
            new Color(56, 180, 204),
            new Color(60, 60, 60),
            new Color(0, 0, 0),
            new Color(0, 0, 0),

            new Color(236, 238, 236),
            new Color(168, 204, 236),
            new Color(188, 188, 236),
            new Color(212, 178, 236),
            new Color(236, 174, 236),
            new Color(236, 174, 212),
            new Color(236, 180, 176),
            new Color(228, 196, 144),
            new Color(204, 210, 120),
            new Color(180, 222, 120),
            new Color(168, 226, 144),
            new Color(152, 226, 180),
            new Color(160, 214, 228),
            new Color(160, 162, 160),
            new Color(0, 0, 0),
            new Color(0, 0, 0),
        ];

        _paletteMemory = new byte[32];
    }

    /// <summary>
    /// CLK - the ~21.48 MHz master-clock input. The setter is the divide-by-four
    /// dot-clock engine: no-change writes are ignored, every genuine transition
    /// advances the half-period counter, and one PPU dot (<see cref="CycleDot"/>)
    /// runs whenever 8 half-periods (one dot = 4 master periods) have elapsed.
    /// The system services the PPU's external address/data bus off the /RD and
    /// /WR pins the dot moves, so there is no per-dot return value any more.
    /// </summary>
    public bool Clk
    {
        get => _clk; // TODO: Shouldn't be accessible
        set
        {
            if (_clk == value)
            {
                return;
            }

            _clk = value;

            if (++_clkDivideCounter == 8)
            {
                _clkDivideCounter = 0;
                CycleDot();
            }
        }
    }

    private void CycleDot()
    {
        // TODO

        // Handle VRAM reads / writes.
        switch (_vramRequestState)
        {
            case VramRequestState.SetupAddressForRead:
                SetupVramRequest(_vramRequestAddress);
                _vramRequestState = VramRequestState.ReadData;
                break;

            case VramRequestState.SetupAddressForWrite:
                SetupVramRequest(_vramRequestAddress);
                _vramRequestState = VramRequestState.WriteData;
                break;

            case VramRequestState.ReadData:
                SetupVramRequestRead(VramReadTarget.VramRead);
                _vramRequestState = VramRequestState.LatchReadData;
                break;

            case VramRequestState.LatchReadData:
                // NesSystem drove the fetched byte back onto AD0-7 in response
                // to the /RD asserted on the previous dot; latch it as the new
                // $2007 read buffer (vram_access items 3-7).
                _ppuReadBuffer = _adBus.Data;
                _ppuRd = true;
                _vramRequestState = VramRequestState.None;
                break;

            case VramRequestState.WriteData:
                SetupVramRequestWrite(_vramRequestData);
                if ((_vramRequestAddress >> 8) == 0x3F)
                {
                    // PPU /WR pin is not active for palette addresses.
                    _ppuWr = true;
                }
                _vramRequestState = VramRequestState.None;
                break;
        }

        // Per-dot background render pipeline: nametable / attribute / pattern-table
        // fetches over the multiplexed PPU bus, the pattern + attribute shift registers,
        // scroll-address updates and the background pixel mux.
        RenderTick();

        // Update the composite-video region / DAC-tap selection for this dot. The
        // per-12x-f_SC-cell samples are pulled from here by NesSystem.CompositeVideo
        // via NextVideoCell().
        UpdateVideoSignal();

        if (CurrentScanline == 241 && CurrentDot == 1)
        {
            StatusRegister.VBlankStarted = true;
        }
        else if (CurrentScanline == 261 && CurrentDot == 1)
        {
            StatusRegister.VBlankStarted = false;
            StatusRegister.Sprite0Hit = false;
            StatusRegister.SpriteOverflow = false;
        }

        // Increment dot and scanline counters. With rendering enabled, odd frames
        // drop the last dot of the pre-render line (scanline 261, dot 340),
        // jumping straight from dot 339 to (0, 0). The shorter odd field shifts
        // the dot<->subcarrier phase relationship every frame, matching real
        // hardware; because the chroma phase counter free-runs, dropping the dot
        // is all that is needed. Which frames count as "odd" is a calibration
        // landmark for the Flawless2C02 comparison (step 6).
        var renderingEnabled = MaskRegister.RenderBackground || MaskRegister.RenderSprites;
        var skipLastDot =
            CurrentScanline == 261 &&
            CurrentDot == 339 &&
            renderingEnabled &&
            (Frames & 1) == 1;

        CurrentDot++;
        if (CurrentDot == 341 || skipLastDot)
        {
            CurrentDot = 0;
            CurrentScanline++;
            if (CurrentScanline == 262)
            {
                CurrentScanline = 0;

                Frames++;
            }
        }

        _nmi = !(StatusRegister.VBlankStarted && CtrlRegister.EnableNmi);

        Cycles++;
    }

    // Runs one CPU-register read/write, off the falling edge of the /DBE pin.
    private void OnDbeActive()
    {
        if (_cpuRw) // Read
        {
            // Where a register (or a bit of one) is not driven by the PPU it
            // reads back from the open-bus decay register. Start from the
            // decayed value; each case overlays and refreshes only its own bits.
            var openBus = ReadOpenBus();
            var result = openBus;

            switch (_cpuAddress)
            {
                case PpuCtrlAddress:   // $2000 - write-only, wholly open bus
                case PpuMaskAddress:   // $2001 - write-only
                case OamAddrAddress:   // $2003 - write-only
                case PpuScrollAddress: // $2005 - write-only
                case PpuAddrAddress:   // $2006 - write-only
                    break;

                case PpuStatusAddress: // $2002 - bits 7-5 defined, 4-0 open bus
                    StatusRegister.Unused = openBus;
                    result = StatusRegister.Data.Value;
                    RefreshOpenBus(result, 0xE0);
                    StatusRegister.VBlankStarted = false;
                    _w = false;
                    break;

                case OamDataAddress: // $2004 - all 8 bits defined, refreshes all
                    result = _objectAttributeMemory[_oamAddress];
                    if ((_oamAddress & 0x03) == 0x02)
                    {
                        // Sprite attribute byte: bits 2-4 are unimplemented in
                        // OAM and always read back 0 (oam_read, ppu_open_bus 10).
                        result &= 0xE3;
                    }
                    RefreshOpenBus(result, 0xFF);
                    break;

                case PpuDataAddress: // $2007
                    result = PpuRead();
                    if ((_v >> 8) == 0x3F)
                    {
                        // Palette read: bits 5-0 come from palette RAM and
                        // refresh the decay register; bits 7-6 are not driven
                        // and read back as open bus (ppu_open_bus 8).
                        result = (byte)((result & 0x3F) | (openBus & 0xC0));
                        RefreshOpenBus(result, 0x3F);
                    }
                    else
                    {
                        RefreshOpenBus(result, 0xFF);
                    }
                    IncrementPpuAddress();
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }

            _cpuData = result;
        }
        else // Write
        {
            // Any PPU-register write drives all 8 open-bus bits to the written
            // value (ppu_open_bus 2), the read-only $2002 included.
            RefreshOpenBus(_cpuData, 0xFF);

            switch (_cpuAddress)
            {
                case PpuCtrlAddress:
                    CtrlRegister.Data.Value = _cpuData;
                    // t: ...GH.. ........ <- d: ......GH
                    // Nametable select (t bits 10-11) = data bits 0-1.
                    _t = (ushort)((_t & 0xF3FF) | ((_cpuData & 0x03) << 10));
                    // TODO: If we're in vblank, and _ppuStatusRegister.VBlankStarted is set, changing NMI flag from 0 to 1 should trigger NMI.
                    break;

                case PpuMaskAddress:
                    MaskRegister.Data.Value = _cpuData;
                    break;

                case PpuStatusAddress: // Read-only
                    break;

                case OamAddrAddress:
                    _oamAddress = _cpuData;
                    break;

                case OamDataAddress:
                    _objectAttributeMemory[_oamAddress] = _cpuData;
                    _oamAddress++;
                    break;

                case PpuScrollAddress:
                    if (!_w)
                    {
                        // First write.
                        // t: ....... ...ABCDE <- d: ABCDE...
                        // x:              FGH <- d: .....FGH
                        _t = (ushort)((_t & 0xFFE0) | (_cpuData >> 3));
                        _x = (byte)(_cpuData & 0x07);
                        _w = true;
                    }
                    else
                    {
                        // Second write.
                        // t: FGH..AB CDE..... <- d: ABCDEFGH
                        _t = (ushort)((_t & 0x0C1F)
                            | ((_cpuData & 0xF8) << 2)
                            | ((_cpuData & 0x07) << 12));
                        _w = false;
                    }
                    break;

                case PpuAddrAddress:
                    if (!_w)
                    {
                        // First write.
                        // t: .CDEFGH ........ <- d: ..CDEFGH
                        //        <unused>     <- d: AB......
                        // t: Z...... ........ <- 0 (bit 14 cleared)
                        _t = (ushort)((_t & 0x00FF) | ((_cpuData & 0x3F) << 8));
                        _w = true;
                    }
                    else
                    {
                        // Second write.
                        // t: ....... ABCDEFGH <- d: ABCDEFGH
                        // v: <...all bits...> <- t: <...all bits...>
                        _t = (ushort)((_t & 0xFF00) | _cpuData);
                        _v = _t;
                        _w = false;
                    }
                    break;

                case PpuDataAddress:
                    PpuWrite(_cpuData);
                    IncrementPpuAddress();
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }

    // The open-bus value as it reads right now: any bit whose last drive-to-1
    // has aged past the decay window is forced back to 0 first.
    private byte ReadOpenBus()
    {
        for (var bit = 0; bit < 8; bit++)
        {
            if ((_openBus & (1 << bit)) != 0 &&
                Cycles - _openBusSetCycle[bit] >= OpenBusDecayCycles)
            {
                _openBus &= (byte)~(1 << bit);
            }
        }
        return _openBus;
    }

    // Drive the masked bits of the open-bus register to <paramref name="value"/>;
    // every masked bit driven to 1 restarts that bit's decay timer.
    private void RefreshOpenBus(byte value, byte mask)
    {
        _openBus = (byte)((_openBus & ~mask) | (value & mask));
        for (var bit = 0; bit < 8; bit++)
        {
            if ((mask & value & (1 << bit)) != 0)
            {
                _openBusSetCycle[bit] = Cycles;
            }
        }
    }

    private void IncrementPpuAddress()
    {
        _v += (CtrlRegister.VRamAddressIncrementMode == VRamAddressIncrementMode.Add32)
            ? (ushort)32
            : (ushort)1;
    }

    private byte ReadPaletteMemory(ushort address)
    {
        if (MaskRegister.Grayscale)
        {
            address &= 0x30;
        }
        return _paletteMemory[GetPaletteAddress(address)];
    }

    private void SetupVramRequest(ushort address)
    {
        _adBus.Address = address;
        _ppuAle = true;
        _ppuRd = true;
        _ppuWr = true;
    }

    private void SetupVramRequestRead(VramReadTarget target)
    {
        _ppuAle = false;
        _ppuRd = false;
        _ppuWr = true;

        _vramReadTarget = target;
    }

    private void SetupVramRequestWrite(byte data)
    {
        _adBus.Data = data;
        _ppuAle = false;
        _ppuRd = true;
        _ppuWr = false;
    }

    private enum VramReadTarget
    {
        VramRead
    }

    private enum VramRequestState
    {
        None,
        SetupAddressForRead,
        SetupAddressForWrite,
        ReadData,
        LatchReadData,
        WriteData,
    }

    private byte PpuRead()
    {
        // Kick off the real read on the PPU bus. Even for a palette address the
        // bus reads the nametable byte "underneath" (the $2Fxx mirror), and that
        // is what ends up in the read buffer - see vram_access items 6-7.
        _vramRequestState = VramRequestState.SetupAddressForRead;
        _vramRequestAddress = _v;

        if ((_v >> 8) == 0x3F)
        {
            // Palette reads are not delayed through the buffer.
            return ReadPaletteMemory(_v);
        }

        // Non-palette reads return the previous buffer contents; the fetch above
        // refills the buffer one PPU fetch later (VramRequestState.LatchReadData).
        return _ppuReadBuffer;
    }

    private void PpuWrite(byte data)
    {
        _vramRequestState = VramRequestState.SetupAddressForWrite;
        _vramRequestAddress = _v;
        _vramRequestData = data;

        if ((_v >> 8) == 0x3F)
        {
            _paletteMemory[GetPaletteAddress(_v)] = data;
        }
    }

    private static ushort GetPaletteAddress(ushort address)
    {
        address &= 0x1F;
        switch (address)
        {
            case 0x10:
                address = 0x00;
                break;
            case 0x14:
                address = 0x04;
                break;
            case 0x18:
                address = 0x08;
                break;
            case 0x1C:
                address = 0x0C;
                break;
        }
        return address;
    }

    internal void CreateDebuggerWindows(List<DebuggerWindow> result)
    {
        result.Add(new PpuStateWindow(this));
        result.Add(new PaletteWindow(this));
    }

    internal Color GetColor(ushort address)
    {
        var paletteId = ReadPaletteMemory(address);
        return _systemPalette[paletteId];
    }

    /// <summary>
    /// The hardware-derived RGB the 2C02's built-in palette maps a 6-bit NES
    /// colour code ($00-$3F) to. This is the reference the composite-video
    /// decode is checked against - what a code is <em>supposed</em> to look like
    /// once it has gone out as an analog waveform and come back through the
    /// Television's NTSC decoder.
    /// </summary>
    internal Color GetSystemPaletteEntry(byte code) => _systemPalette[code & 0x3F];
}
