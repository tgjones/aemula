using System;
using System.Collections.Generic;
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

    private byte _currentLatchData;

    // Registers
    internal PpuCtrlRegister CtrlRegister;
    internal PpuMaskRegister MaskRegister;
    internal PpuStatusRegister StatusRegister;

    internal ulong Cycles;
    internal ulong Frames;
    internal ulong CurrentScanline;
    internal ulong CurrentDot;

    // Master-clock divider. The 2C02's master-clock input drives an internal
    // divide-by-four counter whose output is the ~5.37 MHz dot clock; one dot
    // (CycleDot) runs on every fourth Tick(). Starts at 0 so the first Tick()
    // after construction runs a dot.
    private int _dotClockDivider;

    public Ricoh2C02Pins Pins;

    // Pin values. Every hardware pin is a property whose direction matches the
    // real chip; for now each one just forwards to the corresponding Pins field
    // so the refactor can proceed in small diffs.

    /// <summary>R/W̄ - CPU-bus read/write select (input).</summary>
    public bool CpuRw
    {
        set => Pins.CpuRW = value;
    }

    /// <summary>RS0-RS2 - CPU-bus register select (input).</summary>
    public byte CpuAddress
    {
        set => Pins.CpuAddress = value;
    }

    /// <summary>D0-D7 - CPU data bus (bidirectional).</summary>
    public byte CpuData
    {
        get => Pins.CpuData;
        set => Pins.CpuData = value;
    }

    /// <summary>
    /// AD0-AD7 / A8-A13 - the 14-bit VRAM address the PPU is driving (output).
    /// </summary>
    public ushort PpuAddressBus => Pins.PpuAddressData.Address;

    /// <summary>
    /// AD0-AD7 - the multiplexed low-byte data bus: the byte the PPU drives on a
    /// write, or where a read byte is delivered back (bidirectional).
    /// </summary>
    public byte PpuData
    {
        get => Pins.PpuAddressData.Data;
        set => Pins.PpuAddressData.Data = value;
    }

    /// <summary>ALE - address latch enable (output).</summary>
    public bool PpuAle => Pins.PpuAle;

    /// <summary>R̄D̄ - VRAM read strobe, active low (output).</summary>
    public bool PpuRd => Pins.PpuRD;

    /// <summary>W̄R̄ - VRAM write strobe, active low (output).</summary>
    public bool PpuWr => Pins.PpuWR;

    /// <summary>I̅N̅T̅ - connected to the CPU's N̅M̅I̅ pin, active low (output).</summary>
    public bool Nmi => Pins.Nmi;

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
    /// Master-clock entry point, called once per NES master clock cycle
    /// (~21.48 MHz). Advances the internal divide-by-four dot-clock counter and
    /// runs one PPU dot (<see cref="CycleDot"/>) on every fourth call. Returns
    /// <c>true</c> on the ticks where a dot actually ran, so the containing
    /// system knows when to service the PPU's external address/data bus.
    /// </summary>
    public bool Tick()
    {
        if (_dotClockDivider == 0)
        {
            _dotClockDivider = 3;
            CycleDot();
            return true;
        }

        _dotClockDivider--;
        return false;
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
                _vramRequestState = VramRequestState.None;
                break;

            case VramRequestState.WriteData:
                SetupVramRequestWrite(_vramRequestData);
                if ((_vramRequestAddress >> 8) == 0x3F)
                {
                    // PPU /WR pin is not active for palette addresses.
                    Pins.PpuWR = true;
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

        Pins.Nmi = !(StatusRegister.VBlankStarted && CtrlRegister.EnableNmi);

        Cycles++;
    }

    public void CpuCycle()
    {
        ref var pins = ref Pins;

        if (pins.CpuRW) // Read
        {
            var result = _currentLatchData;

            switch (pins.CpuAddress)
            {
                case PpuCtrlAddress: // Write-only
                    break;

                case PpuMaskAddress: // Write-only
                    break;

                case PpuStatusAddress:
                    StatusRegister.Unused = _currentLatchData;
                    result = StatusRegister.Data.Value;
                    StatusRegister.VBlankStarted = false;
                    _w = false;
                    break;

                case OamAddrAddress: // Write-only
                    break;

                case OamDataAddress:
                    result = _objectAttributeMemory[_oamAddress];
                    break;

                case PpuScrollAddress: // Write-only
                    break;

                case PpuAddrAddress: // Write-only
                    break;

                case PpuDataAddress:
                    result = PpuRead();
                    IncrementPpuAddress();
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }

            _currentLatchData = result;

            pins.CpuData = result;
        }
        else // Write
        {
            _currentLatchData = pins.CpuData;

            switch (Pins.CpuAddress)
            {
                case PpuCtrlAddress:
                    CtrlRegister.Data.Value = pins.CpuData;
                    // t: ...GH.. ........ <- d: ......GH
                    // Nametable select (t bits 10-11) = data bits 0-1.
                    _t = (ushort)((_t & 0xF3FF) | ((pins.CpuData & 0x03) << 10));
                    // TODO: If we're in vblank, and _ppuStatusRegister.VBlankStarted is set, changing NMI flag from 0 to 1 should trigger NMI.
                    break;

                case PpuMaskAddress:
                    MaskRegister.Data.Value = pins.CpuData;
                    break;

                case PpuStatusAddress: // Read-only
                    break;

                case OamAddrAddress:
                    _oamAddress = pins.CpuData;
                    break;

                case OamDataAddress:
                    _objectAttributeMemory[_oamAddress] = pins.CpuData;
                    _oamAddress++;
                    break;

                case PpuScrollAddress:
                    if (!_w)
                    {
                        // First write.
                        // t: ....... ...ABCDE <- d: ABCDE...
                        // x:              FGH <- d: .....FGH
                        _t = (ushort)((_t & 0xFFE0) | (pins.CpuData >> 3));
                        _x = (byte)(pins.CpuData & 0x07);
                        _w = true;
                    }
                    else
                    {
                        // Second write.
                        // t: FGH..AB CDE..... <- d: ABCDEFGH
                        _t = (ushort)((_t & 0x0C1F)
                            | ((pins.CpuData & 0xF8) << 2)
                            | ((pins.CpuData & 0x07) << 12));
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
                        _t = (ushort)((_t & 0x00FF) | ((pins.CpuData & 0x3F) << 8));
                        _w = true;
                    }
                    else
                    {
                        // Second write.
                        // t: ....... ABCDEFGH <- d: ABCDEFGH
                        // v: <...all bits...> <- t: <...all bits...>
                        _t = (ushort)((_t & 0xFF00) | pins.CpuData);
                        _v = _t;
                        _w = false;
                    }
                    break;

                case PpuDataAddress:
                    PpuWrite(pins.CpuData);
                    IncrementPpuAddress();
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
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
        Pins.PpuAddressData.Address = address;
        Pins.PpuAle = true;
        Pins.PpuRD = true;
        Pins.PpuWR = true;
    }

    private void SetupVramRequestRead(VramReadTarget target)
    {
        Pins.PpuAle = false;
        Pins.PpuRD = false;
        Pins.PpuWR = true;

        _vramReadTarget = target;
    }

    private void SetupVramRequestWrite(byte data)
    {
        Pins.PpuAddressData.Data = data;
        Pins.PpuAle = false;
        Pins.PpuRD = true;
        Pins.PpuWR = false;
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
        WriteData,
    }

    private byte PpuRead()
    {
        ref var pins = ref Pins;
        var result = _ppuReadBuffer;

        _vramRequestState = VramRequestState.SetupAddressForRead;
        _vramRequestAddress = _v;

        if ((_v >> 8) == 0x3F)
        {
            result = _ppuReadBuffer = ReadPaletteMemory(_v);
        }

        return result;
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
