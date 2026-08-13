namespace Aemula.Emulation.Chips;

/// <summary>
/// 4-bit bidirectional universal shift register with synchronous parallel
/// load and an active-low, asynchronous clear.
/// </summary>
public sealed class Ttl74194Chip
{
    public bool A { private get; set; }
    public bool B { private get; set; }
    public bool C { private get; set; }
    public bool D { private get; set; }

    /// <summary>
    /// Serial data input, shifted into <see cref="Qa"/> during a shift-right
    /// operation (<see cref="S0"/> asserted, <see cref="S1"/> not).
    /// </summary>
    public bool Dsr { private get; set; }

    /// <summary>
    /// Serial data input, shifted into <see cref="Qd"/> during a shift-left
    /// operation (<see cref="S1"/> asserted, <see cref="S0"/> not).
    /// </summary>
    public bool Dsl { private get; set; }

    public bool S0 { private get; set; }
    public bool S1 { private get; set; }

    private bool _clr = true;
    public bool Clr
    {
        set
        {
            _clr = value;

            if (!_clr)
            {
                Qa = false;
                Qb = false;
                Qc = false;
                Qd = false;
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

            if (S1 && S0)
            {
                // Parallel load.
                Qa = A;
                Qb = B;
                Qc = C;
                Qd = D;
            }
            else if (S1)
            {
                // Shift left: DSL -> QD -> QC -> QB -> QA.
                Qa = Qb;
                Qb = Qc;
                Qc = Qd;
                Qd = Dsl;
            }
            else if (S0)
            {
                // Shift right: DSR -> QA -> QB -> QC -> QD.
                Qd = Qc;
                Qc = Qb;
                Qb = Qa;
                Qa = Dsr;
            }
            // Else: hold, no change.
        }
    }

    public bool Qa { get; private set; }
    public bool Qb { get; private set; }
    public bool Qc { get; private set; }
    public bool Qd { get; private set; }
}
