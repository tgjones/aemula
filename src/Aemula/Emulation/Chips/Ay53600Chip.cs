namespace Aemula.Emulation.Chips;

/// <summary>
/// AY-5-3600 keyboard matrix encoder, configured per Apple's custom mask
/// (internal oscillator, "Any Key Down" option pin, 7-bit ASCII on B1-B7).
/// See datasheet: http://www.applelogic.org/files/AY3600.pdf
///
/// Models the chip's X-line scan, RC-timed keybounce debounce, and code
/// latch/strobe behaviour. The 9x10 physical crosspoint-to-key mapping is
/// board wiring, not chip internals, so it's supplied by whoever's driving
/// <see cref="SetPressedKey"/> (the Apple II keyboard has no per-crosspoint
/// diodes, so it's spec'd as 2-key rollover anyway - only a single "current"
/// key position is tracked here, rather than modelling the full matrix and
/// its no-diode phantom-key artifacts).
///
/// The key-code ROM table itself (<see cref="Lookup"/>) is treated as chip
/// internals - unlike the character-generator ROM (a separate, swappable
/// board part, modelled as external data), the AY-5-3600's encoding table is
/// mask-programmed into the die itself.
/// </summary>
public sealed class Ay53600Chip
{
    // The real chip's internal oscillator runs ~80-90KHz (external R/C on
    // the Apple II+); its keybounce delay is a separate ~7-8ms RC timer.
    // Approximated here as a fixed count of this chip's own Tick() calls -
    // the system is expected to call Tick() at roughly the chip's real scan
    // rate, making this threshold correspond to about 8ms.
    private const int DebounceTicks = 640;

    public bool Shift { private get; set; }
    public bool Control { private get; set; }

    private int _pressedX = -1;
    private int _pressedY = -1;

    private bool _debouncing;
    private int _debounceCounter;
    private bool _keyLatched;

    /// <summary>
    /// The X output ring counter's current position (0-8), i.e. which
    /// matrix row the chip is currently scanning.
    /// </summary>
    public int X { get; private set; }

    /// <summary>
    /// AKD: high while any key is held down.
    /// </summary>
    public bool AnyKeyDown { get; private set; }

    /// <summary>
    /// B1-B7: the latched 7-bit ASCII code of the most recently debounced
    /// key. Holds its value until the next key is latched.
    /// </summary>
    public byte Data { get; private set; }

    /// <summary>
    /// Pulses true for one <see cref="Tick"/> when a newly-pressed key
    /// finishes debouncing and <see cref="Data"/> is updated.
    /// </summary>
    public bool DataReady { get; private set; }

    /// <summary>
    /// Reports which matrix crosspoint (if any) is currently shorted by a
    /// held-down key. Pass (-1, -1) for none. Changing the pressed key
    /// resets debounce/latch state, same as a real keyup/keydown would.
    /// </summary>
    public void SetPressedKey(int x, int y)
    {
        if (x == _pressedX && y == _pressedY)
        {
            return;
        }

        _pressedX = x;
        _pressedY = y;
        _debouncing = false;
        _debounceCounter = 0;
        _keyLatched = false;
    }

    public void Tick()
    {
        X = (X + 1) % 9;

        DataReady = false;

        if (_pressedX < 0)
        {
            AnyKeyDown = false;
            _debouncing = false;
            return;
        }

        AnyKeyDown = true;

        if (_keyLatched)
        {
            return;
        }

        if (!_debouncing && X == _pressedX)
        {
            _debouncing = true;
            _debounceCounter = 0;
        }

        if (!_debouncing)
        {
            return;
        }

        _debounceCounter++;

        if (_debounceCounter >= DebounceTicks)
        {
            Data = Lookup(_pressedX, _pressedY, Shift, Control);
            DataReady = true;
            _keyLatched = true;
        }
    }

    /// <summary>
    /// The Apple II+'s ROM table: (X, Y) matrix position plus Shift/Control
    /// to 7-bit ASCII. Letters are uppercase-only (no lowercase charset on
    /// the II+), and Control produces the standard Ctrl-code (value &amp;
    /// 0x1F) for any key. RETURN/ESC/SPACE/the arrow keys are fixed codes
    /// regardless of modifiers, matching Jim Sather's "Understanding the
    /// Apple II" Table 7.2. Shifted symbols for the punctuation keys follow
    /// the physical keycap legends (e.g. the ":"/"*" key); plain digit keys
    /// have no shifted symbol on this keyboard and repeat unshifted.
    /// </summary>
    private static byte Lookup(int x, int y, bool shift, bool control)
    {
        byte Letter(char upper) => control ? (byte)(upper & 0x1F) : (byte)upper;
        byte Punct(char unshifted, char shifted) => Letter(shift ? shifted : unshifted);

        return (x, y) switch
        {
            // X0: digit/punctuation row.
            (0, 0) => Punct('3', '3'),
            (0, 1) => Punct('4', '4'),
            (0, 2) => Punct('5', '5'),
            (0, 3) => Punct('6', '6'),
            (0, 4) => Punct('7', '7'),
            (0, 5) => Punct('8', '8'),
            (0, 6) => Punct('9', '9'),
            (0, 7) => Punct('0', '0'),
            (0, 8) => Punct(':', '*'),
            (0, 9) => Punct('-', '='),

            // X1: QWERTYUIOP.
            (1, 0) => Letter('Q'),
            (1, 1) => Letter('W'),
            (1, 2) => Letter('E'),
            (1, 3) => Letter('R'),
            (1, 4) => Letter('T'),
            (1, 5) => Letter('Y'),
            (1, 6) => Letter('U'),
            (1, 7) => Letter('I'),
            (1, 8) => Letter('O'),
            (1, 9) => Letter('P'),

            // X2: DFGHJKL;+ and the two arrow keys.
            (2, 0) => Letter('D'),
            (2, 1) => Letter('F'),
            (2, 2) => Letter('G'),
            (2, 3) => Letter('H'),
            (2, 4) => Letter('J'),
            (2, 5) => Letter('K'),
            (2, 6) => Letter('L'),
            (2, 7) => Punct(';', '+'),
            (2, 8) => 0x08, // Left arrow: fixed, equivalent to Ctrl-H.
            (2, 9) => 0x15, // Right arrow: fixed, equivalent to Ctrl-U.

            // X3: ZXCVBNM,./.
            (3, 0) => Letter('Z'),
            (3, 1) => Letter('X'),
            (3, 2) => Letter('C'),
            (3, 3) => Letter('V'),
            (3, 4) => Letter('B'),
            (3, 5) => Letter('N'),
            (3, 6) => Letter('M'),
            (3, 7) => Punct(',', '<'),
            (3, 8) => Punct('.', '>'),
            (3, 9) => Punct('/', '?'),

            // X4: S21 ESC A SPACE ... RETURN.
            (4, 0) => Letter('S'),
            (4, 1) => Punct('2', '2'),
            (4, 2) => Punct('1', '1'),
            (4, 3) => 0x1B, // Escape: fixed.
            (4, 4) => Letter('A'),
            (4, 5) => 0x20, // Space: fixed.
            (4, 9) => 0x0D, // Return: fixed.

            _ => 0,
        };
    }
}
