namespace Aemula.Emulation.Chips;

/// <summary>
/// Dual D-type positive-edge-triggered flip-flop with asynchronous set and
/// reset. Unlike <see cref="Ttl7474Chip"/> these are active high.
/// </summary>
public sealed class Cd4013Chip
{
    public bool D1 { private get; set; }

    /// <summary>
    /// Set 1, active high. Asynchronous; overrides the clock and data.
    /// </summary>
    public bool Set1
    {
        private get => _set1;
        set
        {
            _set1 = value;
            UpdateAsync1();
        }
    }

    private bool _set1;

    /// <summary>
    /// Reset 1, active high. Asynchronous; overrides the clock and data.
    /// </summary>
    public bool Reset1
    {
        private get => _reset1;
        set
        {
            _reset1 = value;
            UpdateAsync1();
        }
    }

    private bool _reset1;

    private bool _clk1;
    public bool Clk1
    {
        set
        {
            var risingEdge = value && !_clk1;
            _clk1 = value;

            if (risingEdge && !_set1 && !_reset1)
            {
                Q1 = D1;
                Qn1 = !D1;
            }
        }
    }

    private void UpdateAsync1()
    {
        if (_set1 || _reset1)
        {
            // Set and reset together force both outputs high.
            Q1 = _set1;
            Qn1 = _reset1;
        }
    }

    public bool Q1 { get; private set; }
    public bool Qn1 { get; private set; } = true;

    public bool D2 { private get; set; }

    /// <summary>
    /// Set 2, active high. Asynchronous; overrides the clock and data.
    /// </summary>
    public bool Set2
    {
        private get => _set2;
        set
        {
            _set2 = value;
            UpdateAsync2();
        }
    }

    private bool _set2;

    /// <summary>
    /// Reset 2, active high. Asynchronous; overrides the clock and data.
    /// </summary>
    public bool Reset2
    {
        private get => _reset2;
        set
        {
            _reset2 = value;
            UpdateAsync2();
        }
    }

    private bool _reset2;

    private bool _clk2;
    public bool Clk2
    {
        set
        {
            var risingEdge = value && !_clk2;
            _clk2 = value;

            if (risingEdge && !_set2 && !_reset2)
            {
                Q2 = D2;
                Qn2 = !D2;
            }
        }
    }

    private void UpdateAsync2()
    {
        if (_set2 || _reset2)
        {
            // Set and reset together force both outputs high.
            Q2 = _set2;
            Qn2 = _reset2;
        }
    }

    public bool Q2 { get; private set; }
    public bool Qn2 { get; private set; } = true;
}
