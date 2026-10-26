namespace Aemula.Emulation.Chips;

/// <summary>
/// Octal buffer/line driver with tri-state outputs, split into two independently
/// enabled groups of four: <see cref="G1"/> gates <see cref="Y1"/>-<see cref="Y4"/>,
/// <see cref="G2"/> gates <see cref="Y5"/>-<see cref="Y8"/>.
/// </summary>
public sealed class Ttl74244Chip
{
    /// <summary>
    /// Enable for Y1-Y4, active low. High makes those outputs high-impedance.
    /// </summary>
    public bool G1 { private get; set; }

    /// <summary>
    /// Enable for Y5-Y8, active low. High makes those outputs high-impedance.
    /// </summary>
    public bool G2 { private get; set; }

    public bool A1 { private get; set; }
    public bool? Y1 => G1 ? null : A1;

    public bool A2 { private get; set; }
    public bool? Y2 => G1 ? null : A2;

    public bool A3 { private get; set; }
    public bool? Y3 => G1 ? null : A3;

    public bool A4 { private get; set; }
    public bool? Y4 => G1 ? null : A4;

    public bool A5 { private get; set; }
    public bool? Y5 => G2 ? null : A5;

    public bool A6 { private get; set; }
    public bool? Y6 => G2 ? null : A6;

    public bool A7 { private get; set; }
    public bool? Y7 => G2 ? null : A7;

    public bool A8 { private get; set; }
    public bool? Y8 => G2 ? null : A8;
}
