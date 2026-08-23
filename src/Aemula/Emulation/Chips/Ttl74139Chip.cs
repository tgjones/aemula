namespace Aemula.Emulation.Chips;

/// <summary>
/// Dual 2-to-4 line decoder/demultiplexer with active-low outputs.
/// </summary>
public sealed class Ttl74139Chip
{
    public bool A1 { private get; set; }
    public bool B1 { private get; set; }

    /// <summary>
    /// Enable, active low.
    /// </summary>
    public bool G1 { private get; set; }

    public bool Y1_0 => !(!G1 && !A1 && !B1);
    public bool Y1_1 => !(!G1 && A1 && !B1);
    public bool Y1_2 => !(!G1 && !A1 && B1);
    public bool Y1_3 => !(!G1 && A1 && B1);

    public bool A2 { private get; set; }
    public bool B2 { private get; set; }

    /// <summary>
    /// Enable, active low.
    /// </summary>
    public bool G2 { private get; set; }

    public bool Y2_0 => !(!G2 && !A2 && !B2);
    public bool Y2_1 => !(!G2 && A2 && !B2);
    public bool Y2_2 => !(!G2 && !A2 && B2);
    public bool Y2_3 => !(!G2 && A2 && B2);
}
