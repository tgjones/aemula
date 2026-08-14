namespace Aemula.Emulation.Chips;

/// <summary>
/// 4-bit parallel-access shift register with JK serial input, active-low
/// asynchronous clear, and active-low synchronous shift/load control.
/// </summary>
public sealed class Ttl74S195Chip
{
    public bool J { private get; set; }

    /// <summary>
    /// K, active low (datasheet K'). Together with <see cref="J"/>, controls
    /// Qa per the JK truth table: J=0,K'=1 holds; J=0,K'=0 resets; J=1,K'=1
    /// sets; J=1,K'=0 toggles.
    /// </summary>
    public bool Kn { private get; set; }

    public bool A { private get; set; }
    public bool B { private get; set; }
    public bool C { private get; set; }
    public bool D { private get; set; }

    /// <summary>
    /// Shift/load, active low. When low, a rising clock edge synchronously
    /// loads A-D into Qa-Qd. When high, a rising clock edge shifts right,
    /// with the new Qa determined by <see cref="J"/> and <see cref="Kn"/>.
    /// </summary>
    public bool ShLd { private get; set; } = true;

    private bool _clr = true;
    public bool Clr
    {
        private get => _clr;
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

            if (!risingEdge || !Clr)
            {
                return;
            }

            if (!ShLd)
            {
                Qa = A;
                Qb = B;
                Qc = C;
                Qd = D;
            }
            else
            {
                var newQa = (J, Kn) switch
                {
                    (false, true) => Qa, // Hold.
                    (false, false) => false, // Reset.
                    (true, true) => true, // Set.
                    (true, false) => !Qa, // Toggle.
                };

                Qd = Qc;
                Qc = Qb;
                Qb = Qa;
                Qa = newQa;
            }
        }
    }

    public bool Qa { get; private set; }
    public bool Qb { get; private set; }
    public bool Qc { get; private set; }
    public bool Qd { get; private set; }

    public bool QdInverted => !Qd;
}
