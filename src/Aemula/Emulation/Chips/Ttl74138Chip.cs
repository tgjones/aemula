namespace Aemula.Emulation.Chips;

/// <summary>
/// 3-to-8 line decoder/demultiplexer with active-low outputs.
/// </summary>
public sealed class Ttl74138Chip
{
    public bool A { private get; set; }
    public bool B { private get; set; }
    public bool C { private get; set; }

    /// <summary>
    /// Enable, active high. Must be asserted, along with <see cref="G2A"/>
    /// and <see cref="G2B"/> both being asserted (low), for any output to
    /// be driven active.
    /// </summary>
    public bool G1 { private get; set; }

    /// <summary>
    /// Enable, active low.
    /// </summary>
    public bool G2A { private get; set; }

    /// <summary>
    /// Enable, active low.
    /// </summary>
    public bool G2B { private get; set; }

    private bool Enabled => G1 && !G2A && !G2B;

    public bool Y0 => !(Enabled && !A && !B && !C);
    public bool Y1 => !(Enabled && A && !B && !C);
    public bool Y2 => !(Enabled && !A && B && !C);
    public bool Y3 => !(Enabled && A && B && !C);
    public bool Y4 => !(Enabled && !A && !B && C);
    public bool Y5 => !(Enabled && A && !B && C);
    public bool Y6 => !(Enabled && !A && B && C);
    public bool Y7 => !(Enabled && A && B && C);
}
