namespace Aemula.Emulation.Chips;

/// <summary>
/// 4-bit synchronous decade (mod-10) counter with active-low asynchronous
/// clear and active-low synchronous parallel load. Same pin shape as
/// <see cref="Ttl74161Chip"/>, but counts 0-9 and wraps, rather than 0-15.
/// </summary>
public sealed class Ttl74160Chip
{
    public bool A { private get; set; }
    public bool B { private get; set; }
    public bool C { private get; set; }
    public bool D { private get; set; }

    public bool Load { private get; set; } = true;

    /// <summary>
    /// Count enable, parallel. Must be asserted, along with <see cref="Ent"/>,
    /// for the counter to advance on a rising clock edge.
    /// </summary>
    public bool Enp { private get; set; }

    /// <summary>
    /// Count enable, trickle. Must be asserted, along with <see cref="Enp"/>,
    /// for the counter to advance on a rising clock edge. Also gates
    /// <see cref="Rco"/>, which is what lets counters be chained together.
    /// </summary>
    public bool Ent { private get; set; }

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

            if (!Load)
            {
                Qa = A;
                Qb = B;
                Qc = C;
                Qd = D;
            }
            else if (Enp && Ent)
            {
                var count = (byte)((Qa ? 1 : 0) | (Qb ? 2 : 0) | (Qc ? 4 : 0) | (Qd ? 8 : 0));
                count = (byte)((count + 1) % 10);
                Qa = (count & 1) != 0;
                Qb = (count & 2) != 0;
                Qc = (count & 4) != 0;
                Qd = (count & 8) != 0;
            }
        }
    }

    public bool Qa { get; private set; }
    public bool Qb { get; private set; }
    public bool Qc { get; private set; }
    public bool Qd { get; private set; }

    /// <summary>
    /// Ripple carry output. High when the count is at its maximum (9, i.e.
    /// <see cref="Qa"/> and <see cref="Qd"/> both set) and <see cref="Ent"/>
    /// is asserted. Chaining this into the next counter's <see cref="Ent"/>
    /// lets several 74160s count together as one wider decade counter.
    /// </summary>
    public bool Rco => Ent && Qa && Qd;
}
