namespace Aemula.Emulation.Chips;

/// <summary>
/// Dual AND-OR-invert gate, in the LS/S-family arrangement: gate 1 is
/// 2-wide 3-input (<c>Y1 = !((A1 &amp;&amp; B1 &amp;&amp; C1) || (D1 &amp;&amp; E1 &amp;&amp; F1))</c>) and gate 2
/// is 2-wide 2-input (<c>Y2 = !((A2 &amp;&amp; B2) || (C2 &amp;&amp; D2))</c>). The original
/// plain 7451 instead has two 2-wide 2-input gates, which is
/// <see cref="Ttl7450Chip"/> without its expander pins.
/// </summary>
public sealed class Ttl74LS51Chip
{
    public bool A1 { private get; set; }
    public bool B1 { private get; set; }
    public bool C1 { private get; set; }
    public bool D1 { private get; set; }
    public bool E1 { private get; set; }
    public bool F1 { private get; set; }
    public bool Y1 => !((A1 && B1 && C1) || (D1 && E1 && F1));

    public bool A2 { private get; set; }
    public bool B2 { private get; set; }
    public bool C2 { private get; set; }
    public bool D2 { private get; set; }
    public bool Y2 => !((A2 && B2) || (C2 && D2));
}
