namespace Aemula.Emulation.Chips.VideoUla;

/// <summary>
/// The BBC Micro's video ULA (IC6, Ferranti "VIDPROC"). No die-level
/// description of it exists, so this is behavioural, following the cycle
/// structure of Mike Stirling's FPGA re-implementation, which was checked
/// against a real chip.
///
/// It divides the 16MHz crystal oscillator down to the board's clocks. Its
/// 8MHz, 4MHz and 2MHz outputs are the board's only source of clocks: the
/// 6502's clock is made from the 2MHz output by the synchroniser built around
/// IC30/IC34, and the 1MHz bus clock (1MHZE) is that same output divided by
/// two. The 6845's character clock (CCLK) is also made here: 1MHz, or 2MHz
/// when bit 4 of the control register is set.
///
/// The three divided clocks come from one free-running 4-bit counter, so every
/// output changes on a rising edge of the 16MHz clock with the 8MHz output
/// rising on odd counts and the 2MHz output on count 4. The real chip's
/// propagation delays between its outputs are not modelled.
///
/// The same counter times the video. The 6845 is clocked at the end of count
/// 15 (and count 7 with the 2MHz character clock), so that its outputs are
/// settled when the ULA fetches the byte on the video data bus at count 0 (and
/// count 8). The fetched byte is shifted out at the pixel rate (2, 4, 8 or
/// 16MHz, bits 3-2 of the control register); each bit pair of it, taken from
/// the odd bits, selects one of the 16 palette entries, whose inverted colour
/// bits are the RGB outputs. Teletext mode passes the SAA5050's colour inputs
/// through instead. The cursor inverts the output for the characters the
/// cursor segment bits ask for.
/// </summary>
public sealed class VideoUlaChip
{
    private const byte ControlFlash = 0x01;
    private const byte ControlTeletext = 0x02;
    private const byte ControlCrtcClock2MHz = 0x10;
    private const byte ControlCursorSegment2 = 0x20;
    private const byte ControlCursorSegment1 = 0x40;
    private const byte ControlCursorSegment0 = 0x80;

    private readonly byte[] _palette = new byte[16];

    private byte _counter;
    private byte _shiftRegister;
    private bool _delayedDisen;
    private bool _cursorActive;
    private byte _cursorCounter;

    private bool _clk16MHz;

    /// <summary>
    /// The 16MHz crystal oscillator input (pin 8). Everything advances on its
    /// rising edge.
    /// </summary>
    public bool Clk16MHz
    {
        set
        {
            var risingEdge = value && !_clk16MHz;
            _clk16MHz = value;

            if (risingEdge)
            {
                Advance();
            }
        }
    }

    /// <summary>
    /// The active-low chip select (CS, pin 3), driven by the VIDPROC# signal,
    /// which the board only asserts for writes. While it is low a write is
    /// latched from <see cref="Data"/> and <see cref="A0"/> as the 2MHz clock
    /// rises, which is how the CPLD re-implementation of the chip
    /// (jonsole/bbc-vidproc-cpld) times it.
    /// </summary>
    public bool CS { private get; set; } = true;

    /// <summary>
    /// Selects the palette register rather than the control register for a
    /// write (A0, pin 2).
    /// </summary>
    public bool A0 { private get; set; }

    private void Advance()
    {
        var count = _counter;
        var fetch = Fetching;

        if (!CS && (count & 7) == 4)
        {
            if (A0)
            {
                _palette[Data >> 4] = (byte)(Data & 0xF);
            }
            else
            {
                Control = Data;
            }
        }

        // Everything below sees the state from before this clock edge, as
        // the registers of the real chip do.
        var flash = (Control & ControlFlash) != 0;
        var dot = _palette[
            ((_shiftRegister >> 4) & 8) | ((_shiftRegister >> 3) & 4) | ((_shiftRegister >> 2) & 2) | ((_shiftRegister >> 1) & 1)];
        var invert = CursorInvert;

        if ((Control & ControlTeletext) == 0)
        {
            // The cursor can extend outside the displayed area, so it is not
            // gated by DISEN.
            var flashInvert = (dot & 8) != 0 && flash;
            Red = (((dot & 1) == 0) ^ flashInvert && _delayedDisen) ^ invert;
            Green = (((dot & 2) == 0) ^ flashInvert && _delayedDisen) ^ invert;
            Blue = (((dot & 4) == 0) ^ flashInvert && _delayedDisen) ^ invert;
        }
        else
        {
            Red = RedInput ^ invert;
            Green = GreenInput ^ invert;
            Blue = BlueInput ^ invert;
        }

        _delayedDisen = Disen;

        if (fetch)
        {
            _shiftRegister = Data;
        }
        else if (PixelClockEnabled(count))
        {
            _shiftRegister = (byte)((_shiftRegister << 1) | 1);
        }

        if (fetch && (Cursor || _cursorActive))
        {
            var wasActive = _cursorActive;
            _cursorActive = _cursorCounter != 3;
            _cursorCounter = wasActive ? (byte)((_cursorCounter + 1) & 3) : (byte)0;
        }

        _counter = (byte)((count + 1) & 0xF);
    }

    private bool PixelClockEnabled(byte count) => ((Control >> 2) & 3) switch
    {
        3 => true,
        2 => (count & 1) == 0,
        1 => (count & 3) == 0,
        _ => (count & 7) == 0,
    };

    // The cursor is on for one to four characters from the one the 6845 flags:
    // the first two each have a segment bit of their own and the last two
    // share one.
    private bool CursorInvert =>
        _cursorActive
        && (_cursorCounter switch
        {
            0 => (Control & ControlCursorSegment0) != 0,
            1 => (Control & ControlCursorSegment1) != 0,
            _ => (Control & ControlCursorSegment2) != 0,
        });

    /// <summary>
    /// True when the next rising edge of <see cref="Clk16MHz"/> latches
    /// <see cref="Data"/> into the shift register: at count 0 of the divider, and
    /// at count 8 too with the 2MHz character clock.
    /// </summary>
    private bool Fetching => (_counter & 7) == 0 && ((_counter & 8) == 0 || (Control & ControlCrtcClock2MHz) != 0);

    /// <summary>
    /// The data bus (pins 17-24, the BD0-BD7 net): the video byte, sampled at the
    /// fetch, and the data of a register write.
    /// </summary>
    public byte Data { private get; set; }

    /// <summary>
    /// The 6845's cursor output (CURSOR, pin 25).
    /// </summary>
    public bool Cursor { private get; set; }

    /// <summary>
    /// Display enable (DISEN, pin 26), the 6845's DISPEN as gated by IC38 and
    /// IC41.
    /// </summary>
    public bool Disen { private get; set; }

    /// <summary>
    /// The SAA5050's red, green and blue outputs (RedInput, GreenInput and BlueInput: pins 13,
    /// 11 and 9), which replace the shifter's colours in teletext mode.
    /// </summary>
    public bool RedInput { private get; set; }

    public bool GreenInput { private get; set; }

    public bool BlueInput { private get; set; }

    /// <summary>
    /// Red output (pin 14), after the last rising edge of <see cref="Clk16MHz"/>.
    /// </summary>
    public bool Red { get; private set; }

    /// <summary>
    /// Green output (pin 12).
    /// </summary>
    public bool Green { get; private set; }

    /// <summary>
    /// Blue output (pin 10).
    /// </summary>
    public bool Blue { get; private set; }

    /// <summary>
    /// 8MHz clock output (pin 7).
    /// </summary>
    public bool Clock8MHz => (_counter & 1) != 0;

    /// <summary>
    /// 4MHz clock output (pin 6).
    /// </summary>
    public bool Clock4MHz => (_counter & 2) != 0;

    /// <summary>
    /// 2MHz clock output (pin 5).
    /// </summary>
    public bool Clock2MHz => (_counter & 4) != 0;

    /// <summary>
    /// Character clock for the 6845 (CCLK, pin 28).
    /// </summary>
    public bool CrtcClock => (Control & ControlCrtcClock2MHz) != 0
        ? Clock2MHz
        : (_counter & 8) != 0;

    /// <summary>
    /// The control register, written at &amp;FE20.
    /// </summary>
    public byte Control { get; private set; }

    /// <summary>
    /// The four bits last written for a palette entry (&amp;FE21): bit 3 is
    /// the flash bit and bits 2-0 are the inverted blue, green and red.
    /// </summary>
    public byte GetPaletteEntry(int index) => _palette[index & 0xF];
}
