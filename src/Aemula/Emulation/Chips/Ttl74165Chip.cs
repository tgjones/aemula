namespace Aemula.Emulation.Chips;

/// <summary>
/// 8-bit parallel-in/serial-out shift register with an asynchronous,
/// transparent parallel load - unlike <see cref="Ttl74166Chip"/>'s clocked
/// load, <see cref="A"/> through <see cref="H"/> drive the internal stages
/// directly and continuously while <see cref="ShLd"/> is low, the same way
/// <see cref="Ttl7474Chip"/>'s Pre/Clr do - and true+complement serial
/// outputs.
/// </summary>
public sealed class Ttl74165Chip
{
    private bool _a;
    public bool A { private get => _a; set { _a = value; DoAsyncLoadIfSelected(); } }

    private bool _b;
    public bool B { private get => _b; set { _b = value; DoAsyncLoadIfSelected(); } }

    private bool _c;
    public bool C { private get => _c; set { _c = value; DoAsyncLoadIfSelected(); } }

    private bool _d;
    public bool D { private get => _d; set { _d = value; DoAsyncLoadIfSelected(); } }

    private bool _e;
    public bool E { private get => _e; set { _e = value; DoAsyncLoadIfSelected(); } }

    private bool _f;
    public bool F { private get => _f; set { _f = value; DoAsyncLoadIfSelected(); } }

    private bool _g;
    public bool G { private get => _g; set { _g = value; DoAsyncLoadIfSelected(); } }

    private bool _h;
    public bool H { private get => _h; set { _h = value; DoAsyncLoadIfSelected(); } }

    /// <summary>
    /// Serial data input, shifted in during shift mode (see
    /// <see cref="ShLd"/>).
    /// </summary>
    public bool Ser { private get; set; }

    private bool _shLd = true;

    /// <summary>
    /// Shift/load, active low. While low, <see cref="A"/> through
    /// <see cref="H"/> are loaded onto the internal stages directly and
    /// continuously (an asynchronous, transparent load), overriding
    /// <see cref="Clk"/>/<see cref="ClkInh"/> entirely. High selects shift
    /// mode.
    /// </summary>
    public bool ShLd
    {
        private get => _shLd;
        set
        {
            _shLd = value;
            DoAsyncLoadIfSelected();
        }
    }

    /// <summary>
    /// Clock inhibit, active high. While asserted, clock edges have no
    /// effect (shift mode only - irrelevant while <see cref="ShLd"/> is
    /// low).
    /// </summary>
    public bool ClkInh { private get; set; }

    private bool _clk;
    public bool Clk
    {
        set
        {
            var risingEdge = value && !_clk;
            _clk = value;

            if (!risingEdge || !_shLd || ClkInh)
            {
                return;
            }

            // Shift: Ser -> A -> B -> ... -> H.
            _qh = _qg;
            _qg = _qf;
            _qf = _qe;
            _qe = _qd;
            _qd = _qc;
            _qc = _qb;
            _qb = _qa;
            _qa = Ser;
        }
    }

    private void DoAsyncLoadIfSelected()
    {
        if (_shLd)
        {
            return;
        }

        _qa = _a;
        _qb = _b;
        _qc = _c;
        _qd = _d;
        _qe = _e;
        _qf = _f;
        _qg = _g;
        _qh = _h;
    }

    private bool _qa;
    private bool _qb;
    private bool _qc;
    private bool _qd;
    private bool _qe;
    private bool _qf;
    private bool _qg;
    private bool _qh;

    public bool Qh => _qh;
    public bool QhN => !_qh;
}
