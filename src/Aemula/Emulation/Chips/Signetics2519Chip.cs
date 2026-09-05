using System;

namespace Aemula.Emulation.Chips;

/// <summary>
/// 40-position, 6-bit-wide recirculating shift register - six independent
/// 40-bit rings sharing one clock. Unlike <see cref="Signetics2504Chip"/>'s
/// pair of external clock phases, the 2519 generates its own internal
/// two-phase timing from a single external RC/CLK pair, so this model
/// exposes just one clock pin.
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

    private static void Shift(bool[] bits, bool input)
    {
        Array.Copy(bits, 0, bits, 1, Length - 1);
        bits[0] = input;
    }
}
