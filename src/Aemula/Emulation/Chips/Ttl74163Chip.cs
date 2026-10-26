namespace Aemula.Emulation.Chips;

/// <summary>
/// 4-bit synchronous binary counter with active-low synchronous clear and
/// active-low synchronous parallel load. Identical to
/// <see cref="Ttl74161Chip"/> except that clear is synchronous: it only takes
/// effect on a rising clock edge, and takes priority over load and count.
/// </summary>
public sealed class Ttl74163Chip
{
    public bool A { private get; set; }
    public bool B { private get; set; }
    public bool C { private get; set; }
    public bool D { private get; set; }

    public bool Clr { private get; set; } = true;
    public bool Load { private get; set; } = true;

    /// <summary>
    /// Count enable, parallel. Must be asserted, along with <see cref="Ent"/>,
    /// for the counter to advance on a rising clock edge.
    /// </summary>
    public bool Enp { private get; set; }

    /// <summary>
    /// Count enable, trickle. Must be asserted, along with <see cref="Enp"/>,
    /// for the counter to advance. Also gates <see cref="Rco"/>.
    /// </summary>
    public bool Ent { private get; set; }

    private bool _clk;
    public bool Clk
    {
        set
        {
            var risingEdge = value && !_clk;
            _clk = value;

            if (!risingEdge)
            {
                return;
            }

            if (!Clr)
            {
                Qa = false;
                Qb = false;
                Qc = false;
                Qd = false;
            }
            else if (!Load)
            {
                Qa = A;
                Qb = B;
                Qc = C;
                Qd = D;
            }
            else if (Enp && Ent)
            {
                var count = (byte)((Qa ? 1 : 0) | (Qb ? 2 : 0) | (Qc ? 4 : 0) | (Qd ? 8 : 0));
                count = (byte)((count + 1) & 0xF);
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
    /// Ripple carry output. High when the count is at its maximum (1111) and
    /// <see cref="Ent"/> is asserted.
    /// </summary>
    public bool Rco => Ent && Qa && Qb && Qc && Qd;
}
