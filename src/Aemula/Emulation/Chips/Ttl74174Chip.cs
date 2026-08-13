namespace Aemula.Emulation.Chips;

/// <summary>
/// Hex D-type positive-edge-triggered flip-flop with a common clock and a
/// common active-low, asynchronous clear.
/// </summary>
public sealed class Ttl74174Chip
{
    public bool D1 { private get; set; }
    public bool D2 { private get; set; }
    public bool D3 { private get; set; }
    public bool D4 { private get; set; }
    public bool D5 { private get; set; }
    public bool D6 { private get; set; }

    private bool _clr = true;
    public bool Clr
    {
        set
        {
            _clr = value;

            if (!_clr)
            {
                Q1 = false;
                Q2 = false;
                Q3 = false;
                Q4 = false;
                Q5 = false;
                Q6 = false;
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
                Q2 = D2;
                Q3 = D3;
                Q4 = D4;
                Q5 = D5;
                Q6 = D6;
            }
        }
    }

    public bool Q1 { get; private set; }
    public bool Q2 { get; private set; }
    public bool Q3 { get; private set; }
    public bool Q4 { get; private set; }
    public bool Q5 { get; private set; }
    public bool Q6 { get; private set; }
}
