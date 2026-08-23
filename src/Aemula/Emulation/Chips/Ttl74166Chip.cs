namespace Aemula.Emulation.Chips;

/// <summary>
/// 8-bit parallel-in/serial-out shift register with active-low,
/// asynchronous clear. Only the last stage's output, <see cref="Qh"/>, is
/// brought out - the real chip doesn't expose the intermediate stages.
/// </summary>
public sealed class Ttl74166Chip
{
    public bool A { private get; set; }
    public bool B { private get; set; }
    public bool C { private get; set; }
    public bool D { private get; set; }
    public bool E { private get; set; }
    public bool F { private get; set; }
    public bool G { private get; set; }
    public bool H { private get; set; }

    /// <summary>
    /// Serial data input, shifted in during shift mode (see
    /// <see cref="ShLd"/>).
    /// </summary>
    public bool Ser { private get; set; }

    /// <summary>
    /// Shift/load. High selects shift mode (serial data shifts in from
    /// <see cref="Ser"/>); low selects parallel load mode (<see cref="A"/>
    /// through <see cref="H"/> are loaded instead). Either way, this takes
    /// effect on the next rising clock edge, same as everything else on
    /// this chip except <see cref="Clr"/>.
    /// </summary>
    public bool ShLd { private get; set; }

    /// <summary>
    /// Clock inhibit, active high. While asserted, clock edges have no
    /// effect.
    /// </summary>
    public bool ClkInh { private get; set; }

    private bool _clr = true;
    public bool Clr
    {
        set
        {
            _clr = value;

            if (!_clr)
            {
                _qa = false;
                _qb = false;
                _qc = false;
                _qd = false;
                _qe = false;
                _qf = false;
                _qg = false;
                _qh = false;
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

            if (!risingEdge || !_clr || ClkInh)
            {
                return;
            }

            if (ShLd)
            {
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
            else
            {
                // Parallel load.
                _qa = A;
                _qb = B;
                _qc = C;
                _qd = D;
                _qe = E;
                _qf = F;
                _qg = G;
                _qh = H;
            }
        }
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
