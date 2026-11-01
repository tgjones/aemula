namespace Aemula.Emulation.Chips;

/// <summary>
/// 4-bit synchronous binary counter with active-low synchronous clear and
/// active-low synchronous parallel load. Identical to
/// <see cref="Ttl74161Chip"/> except that clear is synchronous: it only takes
/// effect on a rising clock edge, and takes priority over load and count.
/// </summary>
public struct Ttl74163Chip
{
    public Ttl74163Chip()
    {
    }

    // The parallel inputs and the count, each held as a 4-bit value (A/Qa
    // in bit 0) so a load or a count is one assignment rather than four.
    private int _inputs;
    private int _count;

    public bool A { set => _inputs = value ? _inputs | 1 : _inputs & ~1; }
    public bool B { set => _inputs = value ? _inputs | 2 : _inputs & ~2; }
    public bool C { set => _inputs = value ? _inputs | 4 : _inputs & ~4; }
    public bool D { set => _inputs = value ? _inputs | 8 : _inputs & ~8; }

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
                _count = 0;
            }
            else if (!Load)
            {
                _count = _inputs;
            }
            else if (Enp && Ent)
            {
                _count = (_count + 1) & 0xF;
            }
        }
    }

    public bool Qa => (_count & 1) != 0;
    public bool Qb => (_count & 2) != 0;
    public bool Qc => (_count & 4) != 0;
    public bool Qd => (_count & 8) != 0;

    /// <summary>
    /// Ripple carry output. High when the count is at its maximum (1111) and
    /// <see cref="Ent"/> is asserted.
    /// </summary>
    public bool Rco => Ent && _count == 0xF;
}
