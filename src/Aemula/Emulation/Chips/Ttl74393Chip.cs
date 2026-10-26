namespace Aemula.Emulation.Chips;

/// <summary>
/// Dual 4-bit binary ripple counter. Each counter advances on a falling edge
/// of its clock and is cleared asynchronously by a high clear input. The
/// ripple delay between stages is not modelled: all four outputs change
/// together.
/// </summary>
public sealed class Ttl74393Chip
{
    private bool _clk1 = true;

    /// <summary>
    /// Clock 1. The counter advances on the falling edge.
    /// </summary>
    public bool Clk1
    {
        set
        {
            var fallingEdge = !value && _clk1;
            _clk1 = value;

            if (fallingEdge && !_clr1)
            {
                _count1 = (_count1 + 1) & 0xF;
            }
        }
    }

    private bool _clr1;

    /// <summary>
    /// Asynchronous clear 1, active high. While high the count is held at zero.
    /// </summary>
    public bool Clr1
    {
        set
        {
            _clr1 = value;

            if (value)
            {
                _count1 = 0;
            }
        }
    }

    private int _count1;
    public bool Qa1 => (_count1 & 1) != 0;
    public bool Qb1 => (_count1 & 2) != 0;
    public bool Qc1 => (_count1 & 4) != 0;
    public bool Qd1 => (_count1 & 8) != 0;

    private bool _clk2 = true;

    /// <summary>
    /// Clock 2. The counter advances on the falling edge.
    /// </summary>
    public bool Clk2
    {
        set
        {
            var fallingEdge = !value && _clk2;
            _clk2 = value;

            if (fallingEdge && !_clr2)
            {
                _count2 = (_count2 + 1) & 0xF;
            }
        }
    }

    private bool _clr2;

    /// <summary>
    /// Asynchronous clear 2, active high. While high the count is held at zero.
    /// </summary>
    public bool Clr2
    {
        set
        {
            _clr2 = value;

            if (value)
            {
                _count2 = 0;
            }
        }
    }

    private int _count2;
    public bool Qa2 => (_count2 & 1) != 0;
    public bool Qb2 => (_count2 & 2) != 0;
    public bool Qc2 => (_count2 & 4) != 0;
    public bool Qd2 => (_count2 & 8) != 0;
}
