namespace Aemula.Emulation.Chips;

/// <summary>
/// 4-to-16 line decoder/demultiplexer with active-low outputs and two
/// active-low enables (unlike the 3-to-8 <see cref="Ttl74138Chip"/>, which
/// splits its two-input enable across one active-high and two active-low
/// pins).
/// </summary>
public sealed class Ttl74154Chip
{
    public bool A { private get; set; }
    public bool B { private get; set; }
    public bool C { private get; set; }
    public bool D { private get; set; }

    /// <summary>
    /// Enable, active low. Must be asserted, along with <see cref="G2"/>,
    /// for any output to be driven active.
    /// </summary>
    public bool G1 { private get; set; }

    /// <summary>
    /// Enable, active low.
    /// </summary>
    public bool G2 { private get; set; }

    private bool Enabled => !G1 && !G2;

    public bool Y0 => !(Enabled && !A && !B && !C && !D);
    public bool Y1 => !(Enabled && A && !B && !C && !D);
    public bool Y2 => !(Enabled && !A && B && !C && !D);
    public bool Y3 => !(Enabled && A && B && !C && !D);
    public bool Y4 => !(Enabled && !A && !B && C && !D);
    public bool Y5 => !(Enabled && A && !B && C && !D);
    public bool Y6 => !(Enabled && !A && B && C && !D);
    public bool Y7 => !(Enabled && A && B && C && !D);
    public bool Y8 => !(Enabled && !A && !B && !C && D);
    public bool Y9 => !(Enabled && A && !B && !C && D);
    public bool Y10 => !(Enabled && !A && B && !C && D);
    public bool Y11 => !(Enabled && A && B && !C && D);
    public bool Y12 => !(Enabled && !A && !B && C && D);
    public bool Y13 => !(Enabled && A && !B && C && D);
    public bool Y14 => !(Enabled && !A && B && C && D);
    public bool Y15 => !(Enabled && A && B && C && D);
}
