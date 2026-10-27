namespace Aemula.Emulation.Chips.Motorola6845;

/// <summary>
/// 6845 CRT controller, in its Hitachi HD6845SP form (the one fitted to the
/// BBC Micro).
///
/// Generates the memory address (<see cref="MA"/>), raster address
/// (<see cref="RA"/>), display enable, horizontal and vertical sync, and
/// cursor signals for a character-based raster display from 18 programmable
/// registers. It is written through a 6502-style bus: an address register
/// (RS low) selects a data register (RS high).
///
/// Timing model: the chip advances one character on the falling edge of
/// <see cref="Clk"/>. The output pins always show the state the counters have
/// just moved to, so <see cref="MA"/> is the address of the character that is
/// about to be fetched. A selected read is presented on <see cref="DB"/> when
/// <see cref="E"/> rises and a selected write is latched when it falls.
///
/// The counter behaviour follows the quirks of the real HD6845SP rather than
/// the idealised datasheet: the last character of a scanline never displays,
/// the "last scanline of the frame" decision is latched one character into the
/// line (so changing R9 or R4 on the last line does not stop the frame ending),
/// vertical adjust is entered and left a character after the compare, the
/// vertical-displayed compare notices a change in the middle of a line, and a
/// vertical sync can start at any character of a row once per row.
///
/// With R8 bit 0 set the chip runs the half-line sync (interlace sync) on even
/// fields and inserts a dummy scanline after vertical adjust on odd ones; with
/// R8 bits 0 and 1 both set (interlace sync and video) RA also steps by two and
/// takes the field number as its low bit.
/// </summary>
public sealed class Motorola6845Chip
{
    // Writable bits of R0-R15.
    private static readonly byte[] RegisterMasks =
    [
        0xFF, 0xFF, 0xFF, 0xFF, 0x7F, 0x1F, 0x7F, 0x7F,
        0xF3, 0x1F, 0x7F, 0x1F, 0x3F, 0xFF, 0x3F, 0xFF,
    ];

    // Blink period selected by R10 bits 5-6, as the frame-count bit that
    // toggles it. Zero means the cursor never shows (mode 1) or always shows
    // (mode 0), handled separately.
    private static readonly byte[] CursorFlashMasks = [0x00, 0x00, 0x08, 0x10];

    private const int RegisterCount = 18;

    private const int HorizontalTotal = 0;
    private const int HorizontalDisplayed = 1;
    private const int HorizontalSyncPosition = 2;
    private const int SyncWidths = 3;
    private const int VerticalTotal = 4;
    private const int VerticalAdjust = 5;
    private const int VerticalDisplayed = 6;
    private const int VerticalSyncPosition = 7;
    private const int ModeControl = 8;
    private const int MaxScanline = 9;
    private const int CursorStart = 10;
    private const int CursorEnd = 11;
    private const int StartAddressHigh = 12;
    private const int StartAddressLow = 13;
    private const int CursorHigh = 14;
    private const int CursorLow = 15;
    private const int LightPenHigh = 16;
    private const int LightPenLow = 17;

    private readonly byte[] _registers = new byte[RegisterCount];

    private byte _addressRegister;

    // Counters.
    private int _horizontalCounter;
    private int _scanlineCounter;
    private int _verticalCounter;
    private int _verticalAdjustCounter;
    private int _hsyncCounter;
    private int _vsyncCounter;
    private int _frameCount;

    // Memory address generation.
    private ushort _address;
    private ushort _lineStartAddress;
    private ushort _nextLineStartAddress;

    // Frame sequencing latches.
    private bool _firstScanline;
    private bool _endOfMainLatched;
    private bool _checkVerticalAdjust;
    private bool _endOfVerticalAdjustLatched;
    private bool _endOfFrameLatched;
    private bool _inVerticalAdjust;

    // Sync generation.
    private bool _inHsync;
    private bool _inLineVsync;
    private bool _inHalfLineVsync;
    private bool _hadVsyncThisRow;
    private bool _hadHalfLineVsyncThisRow;
    private bool _vsync;
    private bool _evenFrameLogic;

    // Display enable, as the AND of these gates DispEn.
    private bool _horizontalDisplay;
    private bool _verticalDisplay;
    private bool _skewDisplay;
    private bool _userDisplay;

    // Cursor.
    private bool _cursorOn;
    private bool _cursorOff;
    private bool _cursorOnThisFrame;

    public Motorola6845Chip()
    {
        ResetCounters();
    }

    // Bus pins.

    /// <summary>
    /// Data bus pins (D0-D7).
    /// </summary>
    public byte DB { get; set; }

    /// <summary>
    /// Register select pin (input). Low selects the address register, high the
    /// register it points at.
    /// </summary>
    public bool RS { private get; set; }

    /// <summary>
    /// Read/write pin (input). Read = true, write = false.
    /// </summary>
    public bool RW { private get; set; }

    /// <summary>
    /// Chip select (input), active low.
    /// </summary>
    public bool Cs { private get; set; } = true;

    private bool _e;

    /// <summary>
    /// Enable pin (input), the bus clock. A selected read is presented on
    /// <see cref="DB"/> as it rises and a selected write is latched as it falls.
    /// </summary>
    public bool E
    {
        get => _e;
        set
        {
            if (_e == value)
            {
                return;
            }

            _e = value;

            if (Cs)
            {
                return;
            }

            if (value)
            {
                if (RW)
                {
                    DB = ReadRegister();
                }
            }
            else if (!RW)
            {
                WriteRegister(DB);
            }
        }
    }

    // Character clock and reset.

    private bool _clk;

    /// <summary>
    /// Character clock input (CLK). The chip advances one character on the
    /// falling edge, unless <see cref="Res"/> is holding it in reset.
    /// </summary>
    public bool Clk
    {
        get => _clk;
        set
        {
            if (_clk == value)
            {
                return;
            }

            _clk = value;

            if (!value && _res)
            {
                Tick();
            }
        }
    }

    private bool _res = true;

    /// <summary>
    /// Reset pin (input), active low. While low every counter is held at zero
    /// and the outputs are inactive, so the first character after release is
    /// the first of a new frame. The registers are not affected.
    /// </summary>
    public bool Res
    {
        get => _res;
        set
        {
            if (_res == value)
            {
                return;
            }

            _res = value;

            if (!value)
            {
                ResetCounters();
            }
        }
    }

    private bool _lpStb;

    /// <summary>
    /// Light pen strobe (input). The rising edge latches the current memory
    /// address into R16/R17.
    /// </summary>
    public bool LpStb
    {
        get => _lpStb;
        set
        {
            if (_lpStb == value)
            {
                return;
            }

            _lpStb = value;

            if (value)
            {
                _registers[LightPenHigh] = (byte)((_address >> 8) & 0x3F);
                _registers[LightPenLow] = (byte)_address;
            }
        }
    }

    // Output pins.

    /// <summary>
    /// Memory address outputs (MA0-MA13).
    /// </summary>
    public ushort MA => _address;

    /// <summary>
    /// Raster address outputs (RA0-RA4). In interlace sync and video mode the
    /// low bit is the field number.
    /// </summary>
    public byte RA => (byte)(InterlacedSyncAndVideo ? _scanlineCounter | (_frameCount & 1) : _scanlineCounter);

    /// <summary>
    /// Display enable (output), active high.
    /// </summary>
    public bool DispEn => _horizontalDisplay && _verticalDisplay && _skewDisplay && _userDisplay;

    /// <summary>
    /// Horizontal sync (output), active high.
    /// </summary>
    public bool HSync => _inHsync;

    /// <summary>
    /// Vertical sync (output), active high.
    /// </summary>
    public bool VSync => _vsync;

    /// <summary>
    /// Cursor (output), active high on the cursor's character and raster
    /// lines, while blink allows it and the character is in the displayed area.
    /// </summary>
    public bool Cursor =>
        _address == CursorAddress
        && _cursorOn
        && !_cursorOff
        && _cursorOnThisFrame
        && _horizontalDisplay
        && _verticalDisplay
        && _horizontalCounter < _registers[HorizontalDisplayed];

    private ushort CursorAddress => (ushort)(((_registers[CursorHigh] << 8) | _registers[CursorLow]) & 0x3FFF);

    private bool InterlacedSyncAndVideo => (_registers[ModeControl] & 3) == 3;

    private bool InterlaceSync => (_registers[ModeControl] & 1) != 0;

    private int HsyncWidth => _registers[SyncWidths] & 0x0F;

    private int VsyncWidth => _registers[SyncWidths] >> 4;

    private byte ReadRegister()
    {
        if (!RS)
        {
            return 0;
        }

        return _addressRegister is >= StartAddressHigh and <= LightPenLow
            ? _registers[_addressRegister]
            : (byte)0;
    }

    private void WriteRegister(byte data)
    {
        if (!RS)
        {
            _addressRegister = (byte)(data & 0x1F);
            return;
        }

        if (_addressRegister >= RegisterMasks.Length)
        {
            return;
        }

        _registers[_addressRegister] = (byte)(data & RegisterMasks[_addressRegister]);

        if (_addressRegister != ModeControl)
        {
            return;
        }

        UpdateVsync();

        var skew = (data >> 4) & 3;
        _userDisplay = skew < 3;
    }

    private void ResetCounters()
    {
        _horizontalCounter = 0;
        _scanlineCounter = 0;
        _verticalCounter = 0;
        _verticalAdjustCounter = 0;
        _hsyncCounter = 0;
        _vsyncCounter = 0;
        _frameCount = 0;

        _endOfMainLatched = false;
        _checkVerticalAdjust = false;
        _endOfVerticalAdjustLatched = false;
        _endOfFrameLatched = false;
        _inVerticalAdjust = false;

        _inHsync = false;
        _inLineVsync = false;
        _inHalfLineVsync = false;
        _vsync = false;

        _cursorOn = false;
        _cursorOff = false;

        _horizontalDisplay = true;
        _skewDisplay = true;
        _userDisplay = ((_registers[ModeControl] >> 4) & 3) < 3;

        StartFrame();
        _address = _lineStartAddress;
    }

    private void StartFrame()
    {
        _verticalCounter = 0;
        _firstScanline = true;
        _nextLineStartAddress = StartAddress;
        _lineStartAddress = _nextLineStartAddress;
        _verticalDisplay = true;

        var cursorFlash = (_registers[CursorStart] >> 5) & 3;
        _cursorOnThisFrame = cursorFlash == 0 || (_frameCount & CursorFlashMasks[cursorFlash]) != 0;
        _evenFrameLogic = (_frameCount & 1) == 0;
    }

    private ushort StartAddress => (ushort)(((_registers[StartAddressHigh] << 8) | _registers[StartAddressLow]) & 0x3FFF);

    private void Tick()
    {
        var skew = (_registers[ModeControl] >> 4) & 3;

        if (_inHsync)
        {
            _hsyncCounter = (_hsyncCounter + 1) & 0x0F;

            if (_hsyncCounter == HsyncWidth)
            {
                _inHsync = false;
            }
        }

        // Delayed display enable due to skew.
        if (_horizontalCounter == skew)
        {
            _skewDisplay = true;
        }

        // Latch next line screen address in case this is the last line of a
        // character row.
        if (_horizontalCounter == _registers[HorizontalDisplayed])
        {
            _nextLineStartAddress = _address;
        }

        // End of horizontal displayed, accounting for skew. The last
        // character of a scanline never displays.
        if (_horizontalCounter == _registers[HorizontalDisplayed] + skew
            || _horizontalCounter == _registers[HorizontalTotal] + skew)
        {
            _horizontalDisplay = false;
            _skewDisplay = false;
        }

        if (_horizontalCounter == _registers[HorizontalSyncPosition] && !_inHsync)
        {
            _inHsync = true;
            _hsyncCounter = 0;
        }

        var vsyncPositionHit = _verticalCounter == _registers[VerticalSyncPosition];

        if (vsyncPositionHit || _inLineVsync || _inHalfLineVsync)
        {
            ClockVsync(vsyncPositionHit);
        }

        // The address counter always increments, inside the display area or not.
        _address = (ushort)((_address + 1) & 0x3FFF);

        // The Hitachi chip decides to end (or never enter) vertical adjust
        // here, one character after checking whether to enter it.
        if (_checkVerticalAdjust)
        {
            _checkVerticalAdjust = false;

            if (_endOfMainLatched)
            {
                if (_verticalAdjustCounter == _registers[VerticalAdjust])
                {
                    _endOfVerticalAdjustLatched = true;
                }

                _verticalAdjustCounter = (_verticalAdjustCounter + 1) & 0x1F;
            }
        }

        // The "last scanline of the frame" state is latched at exactly C0=1,
        // so changing R9 from 0 to 6 on the last scanline does not prevent a
        // new frame from starting.
        if (_horizontalCounter == 1)
        {
            if (_verticalCounter == _registers[VerticalTotal] && _scanlineCounter == _registers[MaxScanline])
            {
                _endOfMainLatched = true;
                _verticalAdjustCounter = 0;
            }

            _checkVerticalAdjust = true;
        }

        if (_horizontalCounter == _registers[HorizontalTotal])
        {
            EndOfScanline();
            _horizontalCounter = 0;
            _horizontalDisplay = true;
        }
        else
        {
            _horizontalCounter = (_horizontalCounter + 1) & 0xFF;
        }

        // The vertical-displayed compare is noticed at any character, even in
        // the middle of a scanline, except on the first scanline of a frame.
        // Blink and interlace stop counting frames if R6 > R4.
        if (_verticalCounter == _registers[VerticalDisplayed] && !_firstScanline && _verticalDisplay)
        {
            _verticalDisplay = false;
            _frameCount++;
        }
    }

    private void ClockVsync(bool positionHit)
    {
        var halfLineHit = _horizontalCounter == (_registers[HorizontalTotal] + 1) >> 1;

        // A vsync starts at any character and scanline position provided none
        // is in progress and none already started in this character row. When
        // one ends and another starts at the same moment the pulse carries on
        // uninterrupted and its counter keeps counting, wrapping at 16.
        var lineVsyncEnding = _inLineVsync && _vsyncCounter == VsyncWidth;

        if (lineVsyncEnding)
        {
            _inLineVsync = false;
        }

        if (positionHit && !_inLineVsync && !_hadVsyncThisRow)
        {
            _inLineVsync = true;

            if (!lineVsyncEnding)
            {
                _hadVsyncThisRow = true;
                _vsyncCounter = 0;
            }
        }

        if (halfLineHit)
        {
            var halfLineVsyncEnding = _inHalfLineVsync && _vsyncCounter == VsyncWidth;

            if (halfLineVsyncEnding)
            {
                _inHalfLineVsync = false;
            }

            if (positionHit && !_inHalfLineVsync && !_hadHalfLineVsyncThisRow)
            {
                _inHalfLineVsync = true;

                if (!halfLineVsyncEnding)
                {
                    _hadHalfLineVsyncThisRow = true;
                }
            }
        }

        // Both syncs run all the time and one is muxed to the pin, so a change
        // of R8 or of field can switch sources mid-pulse.
        UpdateVsync();
    }

    private void UpdateVsync()
    {
        _vsync = InterlaceSync && _evenFrameLogic ? _inHalfLineVsync : _inLineVsync;
    }

    private void EndOfCharacterRow()
    {
        _verticalCounter = (_verticalCounter + 1) & 0x7F;
        _scanlineCounter = 0;
        _hadVsyncThisRow = false;
        _hadHalfLineVsyncThisRow = false;
        _cursorOn = false;
        _cursorOff = false;
    }

    private void EndOfScanline()
    {
        // From the start of the frame the chip passes linearly through normal
        // rendering, the last scanline of it (vertical adjust pending),
        // vertical adjust, the last scanline of that (dummy raster pending)
        // and the dummy raster that interlace adds.
        _firstScanline = false;

        if (_scanlineCounter == _registers[CursorEnd])
        {
            _cursorOff = true;
        }

        _vsyncCounter = (_vsyncCounter + 1) & 0x0F;

        // A scanline compare hit always loads a new character row address,
        // even inside vertical adjust. Inside vertical adjust it does not
        // advance the vertical counter again, but entering it does.
        var maxScanlineHit = _scanlineCounter == _registers[MaxScanline];

        if (maxScanlineHit)
        {
            _lineStartAddress = _nextLineStartAddress;
        }

        _scanlineCounter = InterlacedSyncAndVideo
            ? (_scanlineCounter + 2) & 0x1E
            : (_scanlineCounter + 1) & 0x1F;

        if (!_inVerticalAdjust && maxScanlineHit)
        {
            EndOfCharacterRow();
        }

        if (_endOfMainLatched && !_endOfVerticalAdjustLatched)
        {
            _inVerticalAdjust = true;
        }

        var endOfFrame = false;

        if (_endOfFrameLatched)
        {
            endOfFrame = true;
        }

        if (_endOfVerticalAdjustLatched)
        {
            _inVerticalAdjust = false;

            // The dummy raster that gives interlaced frames their extra half
            // line is inserted at the very end of the frame, after vertical
            // adjust, on every frame ending on the odd field. The frame count
            // is read here, not the parity latched at the start of the frame.
            if (InterlaceSync && (_frameCount & 1) != 0)
            {
                _endOfFrameLatched = true;
            }
            else
            {
                endOfFrame = true;
            }
        }

        if (endOfFrame)
        {
            _endOfMainLatched = false;
            _endOfVerticalAdjustLatched = false;
            _endOfFrameLatched = false;

            EndOfCharacterRow();
            StartFrame();
        }

        _address = _lineStartAddress;

        if (_scanlineCounter == (_registers[CursorStart] & 0x1F))
        {
            _cursorOn = true;
        }
    }
}
