namespace Aemula.Emulation.Chips;

/// <summary>
/// Dual 4-to-1 data selector/multiplexer with select lines shared between
/// both units.
/// </summary>
public sealed class Ttl74153Chip
{
    public bool A { private get; set; }
    public bool B { private get; set; }

    private int SelectedIndex => (A ? 1 : 0) | (B ? 2 : 0);

    public bool C1_0 { private get; set; }
    public bool C1_1 { private get; set; }
    public bool C1_2 { private get; set; }
    public bool C1_3 { private get; set; }

    /// <summary>
    /// Strobe, active low. Must be asserted for <see cref="Y1"/> to reflect
    /// the selected input; otherwise <see cref="Y1"/> is forced low.
    /// </summary>
    public bool G1 { private get; set; }

    public bool Y1 => !G1 && SelectedIndex switch
    {
        0 => C1_0,
        1 => C1_1,
        2 => C1_2,
        _ => C1_3,
    };

    public bool C2_0 { private get; set; }
    public bool C2_1 { private get; set; }
    public bool C2_2 { private get; set; }
    public bool C2_3 { private get; set; }

    /// <summary>
    /// Strobe, active low. Must be asserted for <see cref="Y2"/> to reflect
    /// the selected input; otherwise <see cref="Y2"/> is forced low.
    /// </summary>
    public bool G2 { private get; set; }

    public bool Y2 => !G2 && SelectedIndex switch
    {
        0 => C2_0,
        1 => C2_1,
        2 => C2_2,
        _ => C2_3,
    };
}
