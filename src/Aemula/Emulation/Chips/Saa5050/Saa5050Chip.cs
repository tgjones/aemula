namespace Aemula.Emulation.Chips.Saa5050;

/// <summary>
/// The SAA5050 teletext character generator (IC5), which the BBC Micro uses
/// for Mode 7. No die-level description of it exists, so this is behavioural:
/// the control code handling follows the datasheet and the way jsbeeb, b-em
/// and MAME behave, which were checked against hardware.
///
/// It is given one character code at a time, as the 6845 steps along a row of
/// the screen (<see cref="Data"/>, taken on a rising edge of <see cref="F1"/>),
/// and turns each into six dots of a 10-row cell. A code passes through two
/// stages of the chip before its cell is shown: the edge after the one that
/// takes it decodes it, and the edge after that loads the cell into the output
/// shift register. So a cell is shown two character clocks after its code was
/// taken, which is why the BBC Micro's Mode 7 delays the 6845's cursor by two
/// characters (and the video ULA by one more) to land the cursor on its cell.
///
/// The rows it shows are twice the 6845's raster count: the 6845's row address
/// bit 0 (inverted, as <see cref="Crs"/>) chooses which of two interlaced scan
/// lines of the 20-line cell is drawn,
/// and the dots of the odd lines are the smoothed ("rounded") ones, which join
/// diagonal steps of a letter with half-dots.
///
/// The chip is clocked at 6MHz (<see cref="Tr6"/>) and rounds at twice that
/// rate, so each edge of TR6 moves on a half-dot: a character lasts 12 of them,
/// in which <see cref="Red"/>, <see cref="Green"/> and <see cref="Blue"/> show
/// the cell.
///
/// A control code takes effect either at its own cell ("set-at": steady,
/// conceal, background colours, the hold graphics codes and their release) or
/// from the cell after it ("set-after": colours, flash, size, graphics
/// style). A control code's cell is drawn as a space, or with the last mosaic
/// character while graphics are held. DEW (the 6845's vertical sync) falling
/// starts a new field. LOSE (display enable) is sampled as a code is decoded:
/// a cell decoded while it is low is blank, and the first code decoded after
/// it falls ends the scan line, at which point the colours and attributes
/// return to white on black.
/// </summary>
public sealed class Saa5050Chip
{
    private const int GlyphRows = 20;
    private const int GlyphCount = 96;
    private const int DotsPerCharacter = 12;

    // Set once the tables are built: the first glyph row of each of the 96
    // characters, as 12 half-dots (bit 0 the leftmost), for each of the three
    // sets of shapes.
    private static readonly ushort[] NormalGlyphs = new ushort[GlyphCount * GlyphRows];
    private static readonly ushort[] GraphicsGlyphs = new ushort[GlyphCount * GlyphRows];
    private static readonly ushort[] SeparatedGlyphs = new ushort[GlyphCount * GlyphRows];

    static Saa5050Chip()
    {
        BuildGlyphs(NormalGlyphs, graphics: false, separated: false);
        BuildGlyphs(GraphicsGlyphs, graphics: true, separated: false);
        BuildGlyphs(SeparatedGlyphs, graphics: true, separated: true);
    }

    // The six-dot rows of the 10-row cell of a character, from the
    // character ROM or, for the mosaic characters, from the six blocks the
    // character's bits select.
    private static byte SourceRow(int character, int row, bool graphics, bool separated)
    {
        if (row < 0 || row >= 10)
        {
            return 0;
        }

        if (!graphics || (character & 0x20) != 0)
        {
            return Saa5050CharacterRom.Glyphs[(character * 10) + row];
        }

        // Blocks are two dots wide and three, four and three rows high, with
        // a separated block losing its leftmost column and bottom row.
        var block = row switch
        {
            < 3 => 0,
            < 7 => 1,
            _ => 2,
        };
        var bitLeft = block switch { 0 => 0x01, 1 => 0x04, _ => 0x10 };
        var bitRight = block switch { 0 => 0x02, 1 => 0x08, _ => 0x40 };
        var lastRow = block switch { 0 => 2, 1 => 6, _ => 9 };
        var bottom = separated && row == lastRow;

        var dots = 0;

        if ((character & bitLeft) != 0 && !bottom)
        {
            dots |= separated ? 0b000110 : 0b000111;
        }

        if ((character & bitRight) != 0 && !bottom)
        {
            dots |= separated ? 0b110000 : 0b111000;
        }

        return (byte)dots;
    }

    // Each dot is two half-dots wide.
    private static int HalfDots(int dots)
    {
        var result = 0;

        for (var x = 0; x < 6; x++)
        {
            if ((dots & (1 << x)) != 0)
            {
                result |= 3 << (x * 2);
            }
        }

        return result;
    }

    // Joins the corner of a diagonal: a half-dot is added to a row where the
    // row it is interlaced with has a dot that the row itself continues
    // from at that side.
    private static int Round(int a, int b) =>
        a | ((a >> 1) & b & ~(b >> 1)) | ((a << 1) & b & ~(b << 1));

    private static void BuildGlyphs(ushort[] table, bool graphics, bool separated)
    {
        for (var character = 0; character < GlyphCount; character++)
        {
            for (var row = 0; row < GlyphRows; row++)
            {
                var a = HalfDots(SourceRow(character, row >> 1, graphics, separated));

                // The other line interlaced with this one: the next, for an
                // odd line, and the previous for an even one.
                var partner = row + ((row & 1) != 0 ? 1 : -1);
                var b = HalfDots(SourceRow(character, partner >> 1, graphics, separated));

                // A row of mosaic block is not smoothed.
                var data = !graphics || (character & 0x20) != 0 ? Round(a, b) : a;
                table[(character * GlyphRows) + row] = (ushort)(data & ((1 << DotsPerCharacter) - 1));
            }
        }
    }

    private ushort[] _nextGlyphs = NormalGlyphs;
    private ushort[] _currentGlyphs = NormalGlyphs;
    private ushort[] _heldGlyphs = NormalGlyphs;

    private int _foreground = 7;
    private int _previousForeground;
    private int _background;
    private bool _separated;
    private bool _doubleHeight;
    private bool _previousDoubleHeight;
    private bool _wasDoubleHeight;
    private bool _secondHalfOfDouble;
    private bool _graphics;
    private bool _conceal;
    private bool _flash;
    private bool _holdGraphics;
    private int _heldCharacter = 0x20;
    private int _scanline;
    private int _flashTime;

    private bool _lose;
    private bool _decodedLose;
    private bool _dew;

    // The code taken at the last F1 edge, waiting to be decoded.
    private int _inputCode = 0x20;

    // The cell decoded at the last F1 edge, waiting to be shown.
    private int _nextRow;
    private int _nextForeground;
    private int _nextBackground;

    // The cell in the output shift register.
    private int _cellRow;
    private int _cellForeground;
    private int _cellBackground;
    private int _dot;

    private bool _f1;
    private bool _tr6;

    /// <summary>
    /// The character code (D0-D6, pins 4-10). Bit 7 is not an input.
    /// </summary>
    public byte Data { private get; set; }

    /// <summary>
    /// Character rounding select (CRS, pin 14): the 6845's row address bit 0
    /// inverted. High draws the even line of the cell's two interlaced lines,
    /// low the odd.
    /// </summary>
    public bool Crs { private get; set; } = true;

    /// <summary>
    /// Blanking input (BLAN, pin 25): while it is low the outputs are all low. The
    /// board ties it high.
    /// </summary>
    public bool Blan { private get; set; } = true;

    /// <summary>
    /// Data entry window (DEW, pin 13), the 6845's vertical sync. Its falling
    /// edge starts a field.
    /// </summary>
    public bool Dew
    {
        set
        {
            var wasHigh = _dew;
            _dew = value;

            // This has to be the falling edge for the scan line count to stay
            // in step when the 6845's vertical sync position is later than
            // its vertical total.
            if (!wasHigh || value)
            {
                return;
            }

            _scanline = 0;
            _secondHalfOfDouble = false;

            // Flash cycles every 64 fields, hidden for the first 16.
            if (++_flashTime == 64)
            {
                _flashTime = 0;
            }
        }
    }

    /// <summary>
    /// Load output signal enable (LOSE, pin 26): the 6845's display enable,
    /// sampled as each code is decoded. Its falling edge is the end of a scan
    /// line.
    /// </summary>
    public bool Lose { set => _lose = value; }

    private void EndScanline()
    {
        _foreground = 7;
        _background = 0;
        _holdGraphics = false;
        _heldCharacter = 0x20;
        _nextGlyphs = _heldGlyphs = NormalGlyphs;
        _flash = false;
        _separated = false;
        _graphics = false;
        _conceal = false;
        _doubleHeight = false;

        if (++_scanline == 10)
        {
            _scanline = 0;
            _secondHalfOfDouble = _secondHalfOfDouble ? false : _wasDoubleHeight;
        }

        _wasDoubleHeight = false;
    }

    /// <summary>
    /// Red output (R, pin 24).
    /// </summary>
    public bool Red { get; private set; }

    /// <summary>
    /// Green output (G, pin 23).
    /// </summary>
    public bool Green { get; private set; }

    /// <summary>
    /// Blue output (B, pin 22).
    /// </summary>
    public bool Blue { get; private set; }

    private bool FlashHidden => _flashTime < 16;

    /// <summary>
    /// The character clock (F1, pin 20). Its rising edge shows the first
    /// half-dot of the cell decoded at the edge before, decodes the code taken
    /// at the edge before, and takes the code on <see cref="Data"/>.
    /// </summary>
    public bool F1
    {
        set
        {
            var risingEdge = value && !_f1;
            _f1 = value;

            if (risingEdge)
            {
                ClockCharacter();
            }
        }
    }

    /// <summary>
    /// The 6MHz dot clock (TR6, pin 19): each edge, rising or falling, moves
    /// on to the next of the cell's 12 half-dots; the last stays.
    /// </summary>
    public bool Tr6
    {
        set
        {
            if (value == _tr6)
            {
                return;
            }

            _tr6 = value;
            ClockDot();
        }
    }

    private void ClockCharacter()
    {
        _cellRow = _nextRow;
        _cellForeground = _nextForeground;
        _cellBackground = _nextBackground;
        _dot = 0;
        UpdateOutputs();

        Decode(_inputCode);
        _inputCode = Data & 0x7F;
    }

    private void Decode(int code)
    {
        if (_decodedLose && !_lose)
        {
            EndScanline();
        }

        _decodedLose = _lose;

        _previousDoubleHeight = _doubleHeight;
        _previousForeground = _foreground;
        _currentGlyphs = _nextGlyphs;

        var flashThisCell = _flash;
        var concealThisCell = _conceal;

        if (code < 0x20)
        {
            code = ControlCode(code);
        }
        else if (_graphics)
        {
            if ((code & 0x20) != 0)
            {
                _heldCharacter = code;
                _heldGlyphs = _currentGlyphs;
            }
        }
        else
        {
            _heldCharacter = 0x20;
        }

        // Steady is set-at, so the cell of the code that turns flash off is
        // already steady, where the cell of the one that turns it on is not
        // yet flashing.
        if (flashThisCell && !_flash)
        {
            flashThisCell = false;
        }

        // Conceal is also set-at, but a colour code only reveals from the cell
        // after it.
        if (_conceal)
        {
            concealThisCell = true;
        }

        var scanline = _scanline << 1;

        if (!Crs)
        {
            scanline++;
        }

        if (_previousDoubleHeight)
        {
            scanline >>= 1;

            if (_secondHalfOfDouble)
            {
                scanline += 10;
            }
        }

        var blank = concealThisCell
            || (flashThisCell && FlashHidden)
            || (_secondHalfOfDouble && !_doubleHeight);

        // Outside the window LOSE opens, the output shift register is not
        // loaded and the cell is black.
        if (!_lose)
        {
            _nextRow = 0;
            _nextForeground = 0;
            _nextBackground = 0;
            return;
        }

        _nextRow = blank ? 0 : _currentGlyphs[((code - 0x20) * GlyphRows) + scanline];
        _nextForeground = _previousForeground;
        _nextBackground = _background;
    }

    private void ClockDot()
    {
        if (_dot < DotsPerCharacter - 1)
        {
            _dot++;
        }

        UpdateOutputs();
    }

    private void UpdateOutputs()
    {
        var colour = ((_cellRow >> _dot) & 1) != 0 ? _cellForeground : _cellBackground;

        Red = Blan && (colour & 1) != 0;
        Green = Blan && (colour & 2) != 0;
        Blue = Blan && (colour & 4) != 0;
    }

    private void SetNextGlyphs()
    {
        _nextGlyphs = _graphics
            ? (_separated ? SeparatedGlyphs : GraphicsGlyphs)
            : NormalGlyphs;
    }

    // Applies a control code and returns the code whose shape the cell shows.
    private int ControlCode(int code)
    {
        var wasGraphics = _graphics;
        var wasHoldGraphics = _holdGraphics;

        switch (code)
        {
            case >= 1 and <= 7:
                _graphics = false;
                _foreground = code;
                _conceal = false;
                SetNextGlyphs();
                break;
            case 8:
                _flash = true;
                break;
            case 9:
                _flash = false;
                break;
            case 12:
            case 13:
                _doubleHeight = (code & 1) != 0;

                if (_doubleHeight)
                {
                    _wasDoubleHeight = true;
                }

                break;
            case >= 17 and <= 23:
                _graphics = true;
                _foreground = code & 7;
                _conceal = false;
                SetNextGlyphs();
                break;
            case 24:
                _conceal = true;
                break;
            case 25:
                _separated = false;
                SetNextGlyphs();
                break;
            case 26:
                _separated = true;
                SetNextGlyphs();
                break;
            case 28:
                _background = 0;
                break;
            case 29:
                _background = _foreground;
                break;
            case 30:
                _holdGraphics = true;
                break;
            case 31:
                _holdGraphics = false;
                break;
        }

        if (wasGraphics && (wasHoldGraphics || _holdGraphics) && _doubleHeight == _previousDoubleHeight)
        {
            _currentGlyphs = _heldGlyphs;
            return _heldCharacter;
        }

        _heldCharacter = 0x20;
        return 0x20;
    }
}
