namespace Aemula.Emulation.Chips;

/// <summary>
/// 40-position, 6-bit-wide recirculating shift register - six independent
/// 40-bit rings sharing one clock and one <see cref="Recirculate"/> control.
/// Confirmed from the real Signetics 2518/2519 datasheet: pin 6 ("Clock") is
/// a plain per-bit shift clock, not an internal oscillator, and pin 4
/// ("Recirculate") is a dedicated control shared by all six channels - "RC"
/// as some schematics abbreviate that pin is short for Recirculate, not a
/// resistor-capacitor timing network (a mistake this class's docs used to
/// make). Per the datasheet's truth table, Recirculate=1 holds/recirculates
/// each channel's own last bit on the next clock edge regardless of its In
/// pin; Recirculate=0 writes In instead.
/// </summary>
public sealed class Signetics2519Chip
{
    private const int Length = 40;

    // The six rings, each a circular buffer: a clock edge moves the shared
    // head rather than every bit (the Apple I clocks this 40 times a line).
    // Stage i of a ring - 0 being the one In feeds, Length - 1 the one Out
    // reads - lives at index (_head + i) % Length.
    private readonly bool[] _bits1 = new bool[Length];
    private readonly bool[] _bits2 = new bool[Length];
    private readonly bool[] _bits3 = new bool[Length];
    private readonly bool[] _bits4 = new bool[Length];
    private readonly bool[] _bits5 = new bool[Length];
    private readonly bool[] _bits6 = new bool[Length];
    private int _head;

    public bool In1 { private get; set; }
    public bool In2 { private get; set; }
    public bool In3 { private get; set; }
    public bool In4 { private get; set; }
    public bool In5 { private get; set; }
    public bool In6 { private get; set; }

    public bool Recirculate { private get; set; }

    private int OutIndex => (_head + Length - 1) % Length;

    public bool Out1 => _bits1[OutIndex];
    public bool Out2 => _bits2[OutIndex];
    public bool Out3 => _bits3[OutIndex];
    public bool Out4 => _bits4[OutIndex];
    public bool Out5 => _bits5[OutIndex];
    public bool Out6 => _bits6[OutIndex];

    private bool _clk;

    public bool Clk
    {
        get => _clk;
        set
        {
            var risingEdge = value && !_clk;
            _clk = value;

            if (!risingEdge)
            {
                return;
            }

            // Every ring's last stage becomes its new first stage's
            // neighbour: step the head back one, and the slot it now points
            // at (the old last stage) is where the new bit goes.
            var outIndex = OutIndex;
            _head = outIndex;

            _bits1[outIndex] = Recirculate ? _bits1[outIndex] : In1;
            _bits2[outIndex] = Recirculate ? _bits2[outIndex] : In2;
            _bits3[outIndex] = Recirculate ? _bits3[outIndex] : In3;
            _bits4[outIndex] = Recirculate ? _bits4[outIndex] : In4;
            _bits5[outIndex] = Recirculate ? _bits5[outIndex] : In5;
            _bits6[outIndex] = Recirculate ? _bits6[outIndex] : In6;
        }
    }
}
