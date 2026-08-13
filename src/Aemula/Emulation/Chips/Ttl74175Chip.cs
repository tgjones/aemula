namespace Aemula.Emulation.Chips;

/// <summary>
/// Quad D-type positive-edge-triggered flip-flop with a common clock and a
/// common active-low, asynchronous clear.
/// </summary>
public sealed class Ttl74175Chip
{
    public bool D1 { private get; set; }
    public bool D2 { private get; set; }
    public bool D3 { private get; set; }
    public bool D4 { private get; set; }

    private bool _clr = true;
    public bool Clr
    {
        set
        {
            _clr = value;

            if (!_clr)
            {
                Q1 = false;
                Qn1 = true;
                Q2 = false;
                Qn2 = true;
                Q3 = false;
                Qn3 = true;
                Q4 = false;
                Qn4 = true;
            }
        }
    }

    private bool _clk;
    public bool Clk
    {
        set
        {
            var risingEdge = value && !_clk;
            _clk = value;

            if (risingEdge && _clr)
            {
                Q1 = D1;
                Qn1 = !D1;
                Q2 = D2;
                Qn2 = !D2;
                Q3 = D3;
                Qn3 = !D3;
                Q4 = D4;
                Qn4 = !D4;
            }
        }
    }

    public bool Q1 { get; private set; }
    public bool Qn1 { get; private set; } = true;

    public bool Q2 { get; private set; }
    public bool Qn2 { get; private set; } = true;

    public bool Q3 { get; private set; }
    public bool Qn3 { get; private set; } = true;

    public bool Q4 { get; private set; }
    public bool Qn4 { get; private set; } = true;
}
