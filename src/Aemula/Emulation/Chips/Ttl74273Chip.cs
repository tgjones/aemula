namespace Aemula.Emulation.Chips;

/// <summary>
/// Octal D-type flip-flop with a common clock and active-low asynchronous
/// clear. All eight bits capture their <c>D</c> input on a rising clock edge.
/// </summary>
public sealed class Ttl74273Chip
{
    private bool _clr = true;
    public bool Clr
    {
        private get => _clr;
        set
        {
            _clr = value;

            if (!_clr)
            {
                Q0 = false;
                Q1 = false;
                Q2 = false;
                Q3 = false;
                Q4 = false;
                Q5 = false;
                Q6 = false;
                Q7 = false;
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

            if (!risingEdge || !_clr)
            {
                return;
            }

            Q0 = D0;
            Q1 = D1;
            Q2 = D2;
            Q3 = D3;
            Q4 = D4;
            Q5 = D5;
            Q6 = D6;
            Q7 = D7;
        }
    }

    public bool D0 { private get; set; }
    public bool Q0 { get; private set; }

    public bool D1 { private get; set; }
    public bool Q1 { get; private set; }

    public bool D2 { private get; set; }
    public bool Q2 { get; private set; }

    public bool D3 { private get; set; }
    public bool Q3 { get; private set; }

    public bool D4 { private get; set; }
    public bool Q4 { get; private set; }

    public bool D5 { private get; set; }
    public bool Q5 { get; private set; }

    public bool D6 { private get; set; }
    public bool Q6 { get; private set; }

    public bool D7 { private get; set; }
    public bool Q7 { get; private set; }
}
