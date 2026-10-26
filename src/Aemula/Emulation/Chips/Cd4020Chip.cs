namespace Aemula.Emulation.Chips;

/// <summary>
/// 14-stage binary ripple counter. It advances on a falling clock edge and is
/// cleared asynchronously by a high <see cref="Reset"/>. Only stages 1 and 4
/// to 14 are brought out to pins; stages 2 and 3 exist inside the chip but
/// have no output. Ripple delay is not modelled.
/// </summary>
public sealed class Cd4020Chip
{
    private int _count;

    private bool _clk = true;
    public bool Clk
    {
        set
        {
            var fallingEdge = !value && _clk;
            _clk = value;

            if (fallingEdge && !_reset)
            {
                _count = (_count + 1) & 0x3FFF;
            }
        }
    }

    private bool _reset;
    public bool Reset
    {
        set
        {
            _reset = value;

            if (value)
            {
                _count = 0;
            }
        }
    }

    public bool Q1 => (_count & 1) != 0;
    public bool Q4 => (_count & 8) != 0;
    public bool Q5 => (_count & 16) != 0;
    public bool Q6 => (_count & 32) != 0;
    public bool Q7 => (_count & 64) != 0;
    public bool Q8 => (_count & 128) != 0;
    public bool Q9 => (_count & 256) != 0;
    public bool Q10 => (_count & 512) != 0;
    public bool Q11 => (_count & 1024) != 0;
    public bool Q12 => (_count & 2048) != 0;
    public bool Q13 => (_count & 4096) != 0;
    public bool Q14 => (_count & 8192) != 0;
}
