namespace Aemula.Emulation.Chips;

/// <summary>
/// Dual 2-wide 2-input AND-OR-invert gate: each gate computes
/// <c>Y = !((A &amp;&amp; B) || (C &amp;&amp; D))</c>. On real hardware gate 1 additionally
/// brings out expander pins (X, X-bar) for chaining an external expander IC
/// onto the OR term; the datasheet's own advice when no expander is used is
/// to leave them open, which is the only configuration this codebase needs,
/// so they aren't modelled.
/// </summary>
public sealed class Ttl7450Chip
{
    public bool A1 { private get; set; }
    public bool B1 { private get; set; }
    public bool C1 { private get; set; }
    public bool D1 { private get; set; }
    public bool Y1 => !((A1 && B1) || (C1 && D1));

    public bool A2 { private get; set; }
    public bool B2 { private get; set; }
    public bool C2 { private get; set; }
    public bool D2 { private get; set; }
    public bool Y2 => !((A2 && B2) || (C2 && D2));
}
