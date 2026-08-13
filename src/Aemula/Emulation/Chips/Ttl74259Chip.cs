namespace Aemula.Emulation.Chips;

/// <summary>
/// 8-bit addressable transparent latch. While <see cref="G"/> is asserted
/// and <see cref="Clr"/> is inactive, the latch selected by the 3-bit
/// address continuously tracks <see cref="D"/>; all other latches, and all
/// latches while disabled, hold their last value.
/// </summary>
public sealed class Ttl74259Chip
{
    private bool _a0;
    public bool A0 { set { _a0 = value; Evaluate(); } }

    private bool _a1;
    public bool A1 { set { _a1 = value; Evaluate(); } }

    private bool _a2;
    public bool A2 { set { _a2 = value; Evaluate(); } }

    private bool _d;
    public bool D { set { _d = value; Evaluate(); } }

    /// <summary>
    /// Enable, active low. While asserted (and <see cref="Clr"/> is
    /// inactive), the addressed latch is transparent to <see cref="D"/>.
    /// </summary>
    private bool _g = true;
    public bool G { set { _g = value; Evaluate(); } }

    private bool _clr = true;
    public bool Clr { set { _clr = value; Evaluate(); } }

    private void Evaluate()
    {
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
            return;
        }

        if (_g)
        {
            return;
        }

        var selected = (_a0 ? 1 : 0) | (_a1 ? 2 : 0) | (_a2 ? 4 : 0);

        switch (selected)
        {
            case 0: Q0 = _d; break;
            case 1: Q1 = _d; break;
            case 2: Q2 = _d; break;
            case 3: Q3 = _d; break;
            case 4: Q4 = _d; break;
            case 5: Q5 = _d; break;
            case 6: Q6 = _d; break;
            case 7: Q7 = _d; break;
        }
    }

    public bool Q0 { get; private set; }
    public bool Q1 { get; private set; }
    public bool Q2 { get; private set; }
    public bool Q3 { get; private set; }
    public bool Q4 { get; private set; }
    public bool Q5 { get; private set; }
    public bool Q6 { get; private set; }
    public bool Q7 { get; private set; }
}
