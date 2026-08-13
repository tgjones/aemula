namespace Aemula.Emulation.Chips;

/// <summary>
/// Dual D-type positive-edge-triggered flip-flop with active-low, asynchronous
/// preset and clear.
/// </summary>
public sealed class Ttl7474Chip
{
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
                _q1 = D1;
                _qn1 = !D1;
            }
        }
    }

    private bool _pre1 = true;
    public bool Pre1
    {
        private get => _pre1;
        set
        {
            _pre1 = value;
            UpdateAsync1();
        }
    }

    private bool _clr1 = true;
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
            _q1 = true;
            _qn1 = true;
        }
        else if (!_clr1)
        {
            _q1 = false;
            _qn1 = true;
        }
        else if (!_pre1)
        {
            _q1 = true;
            _qn1 = false;
        }
    }

    private bool _q1;
    public bool Q1 => _q1;

    private bool _qn1 = true;
    public bool Qn1 => _qn1;

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
                _q2 = D2;
                _qn2 = !D2;
            }
        }
    }

    private bool _pre2 = true;
    public bool Pre2
    {
        private get => _pre2;
        set
        {
            _pre2 = value;
            UpdateAsync2();
        }
    }

    private bool _clr2 = true;
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
            _q2 = true;
            _qn2 = true;
        }
        else if (!_clr2)
        {
            _q2 = false;
            _qn2 = true;
        }
        else if (!_pre2)
        {
            _q2 = true;
            _qn2 = false;
        }
    }

    private bool _q2;
    public bool Q2 => _q2;

    private bool _qn2 = true;
    public bool Qn2 => _qn2;
}
