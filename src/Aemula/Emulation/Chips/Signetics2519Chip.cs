using System;

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

    private readonly bool[] _bits1 = new bool[Length];
    private readonly bool[] _bits2 = new bool[Length];
    private readonly bool[] _bits3 = new bool[Length];
    private readonly bool[] _bits4 = new bool[Length];
    private readonly bool[] _bits5 = new bool[Length];
    private readonly bool[] _bits6 = new bool[Length];

    public bool In1 { private get; set; }
    public bool In2 { private get; set; }
    public bool In3 { private get; set; }
    public bool In4 { private get; set; }
    public bool In5 { private get; set; }
    public bool In6 { private get; set; }

    public bool Recirculate { private get; set; }

    public bool Out1 => _bits1[Length - 1];
    public bool Out2 => _bits2[Length - 1];
    public bool Out3 => _bits3[Length - 1];
    public bool Out4 => _bits4[Length - 1];
    public bool Out5 => _bits5[Length - 1];
    public bool Out6 => _bits6[Length - 1];

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

            Shift(_bits1, In1);
            Shift(_bits2, In2);
            Shift(_bits3, In3);
            Shift(_bits4, In4);
            Shift(_bits5, In5);
            Shift(_bits6, In6);
        }
    }

    private void Shift(bool[] bits, bool input)
    {
        var recirculated = bits[Length - 1];
        Array.Copy(bits, 0, bits, 1, Length - 1);
        bits[0] = Recirculate ? recirculated : input;
    }
}
