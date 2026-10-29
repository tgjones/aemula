namespace Aemula.Emulation.Chips;

/// <summary>
/// Dual D-type positive-edge-triggered flip-flop with active-low, asynchronous
/// preset and clear.
/// </summary>
public struct Ttl7474Chip
{
    // Both flip-flops power up with PRE and CLR inactive and /Q high.
    public Ttl7474Chip()
    {
        _pre1 = true;
        _clr1 = true;
        Qn1 = true;
        _pre2 = true;
        _clr2 = true;
        Qn2 = true;
    }

    public bool D1 { private get; set; }

    private bool _clk1;
    public bool Clk1
    {
        set
        {
            var risingEdge = value && !_clk1;
            _clk1 = value;

            if (risingEdge && _pre1 && _clr1)
            {
                Q1 = D1;
                Qn1 = !D1;
            }
        }
    }

    private bool _pre1;
    public bool Pre1
    {
        private get => _pre1;
        set
        {
            _pre1 = value;
            UpdateAsync1();
        }
    }

    private bool _clr1;
    public bool Clr1
    {
        private get => _clr1;
        set
        {
            _clr1 = value;
            UpdateAsync1();
        }
    }

    private void UpdateAsync1()
    {
        if (!_pre1 && !_clr1)
        {
            // Disallowed state: both outputs are forced high while it holds.
            Q1 = true;
            Qn1 = true;
        }
        else if (!_clr1)
        {
            Q1 = false;
            Qn1 = true;
        }
        else if (!_pre1)
        {
            Q1 = true;
            Qn1 = false;
        }
    }

    public bool Q1 { get; private set; }
    public bool Qn1 { get; private set; }

    public bool D2 { private get; set; }

    private bool _clk2;
    public bool Clk2
    {
        set
        {
            var risingEdge = value && !_clk2;
            _clk2 = value;

            if (risingEdge && _pre2 && _clr2)
            {
                Q2 = D2;
                Qn2 = !D2;
            }
        }
    }

    private bool _pre2;
    public bool Pre2
    {
        private get => _pre2;
        set
        {
            _pre2 = value;
            UpdateAsync2();
        }
    }

    private bool _clr2;
    public bool Clr2
    {
        private get => _clr2;
        set
        {
            _clr2 = value;
            UpdateAsync2();
        }
    }

    private void UpdateAsync2()
    {
        if (!_pre2 && !_clr2)
        {
            // Disallowed state: both outputs are forced high while it holds.
            Q2 = true;
            Qn2 = true;
        }
        else if (!_clr2)
        {
            Q2 = false;
            Qn2 = true;
        }
        else if (!_pre2)
        {
            Q2 = true;
            Qn2 = false;
        }
    }

    public bool Q2 { get; private set; }
    public bool Qn2 { get; private set; }
}
