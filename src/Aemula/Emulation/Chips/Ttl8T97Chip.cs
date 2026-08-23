namespace Aemula.Emulation.Chips;

/// <summary>
/// Hex buffer/driver with tri-state outputs.
/// </summary>
public sealed class Ttl8T97Chip
{
    /// <summary>
    /// Output enable, active low. When high, all outputs are
    /// high-impedance (not driving).
    /// </summary>
    public bool Oe { private get; set; }

    public bool A1 { private get; set; }
    public bool? Y1 => Oe ? null : A1;

    public bool A2 { private get; set; }
    public bool? Y2 => Oe ? null : A2;

    public bool A3 { private get; set; }
    public bool? Y3 => Oe ? null : A3;

    public bool A4 { private get; set; }
    public bool? Y4 => Oe ? null : A4;

    public bool A5 { private get; set; }
    public bool? Y5 => Oe ? null : A5;

    public bool A6 { private get; set; }
    public bool? Y6 => Oe ? null : A6;
}
