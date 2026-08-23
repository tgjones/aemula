namespace Aemula.Emulation.Chips;

/// <summary>
/// Quad 2-input multiplexer with tri-state outputs, a single select line
/// shared across all four channels.
/// </summary>
public sealed class Ttl74257Chip
{
    public bool S { private get; set; }

    /// <summary>
    /// Output enable, active low. When high, all outputs are
    /// high-impedance (not driving), represented as <see langword="null"/>.
    /// </summary>
    public bool Oe { private get; set; }

    public bool A1 { private get; set; }
    public bool B1 { private get; set; }
    public bool? Y1 => Oe ? null : (S ? B1 : A1);

    public bool A2 { private get; set; }
    public bool B2 { private get; set; }
    public bool? Y2 => Oe ? null : (S ? B2 : A2);

    public bool A3 { private get; set; }
    public bool B3 { private get; set; }
    public bool? Y3 => Oe ? null : (S ? B3 : A3);

    public bool A4 { private get; set; }
    public bool B4 { private get; set; }
    public bool? Y4 => Oe ? null : (S ? B4 : A4);
}
