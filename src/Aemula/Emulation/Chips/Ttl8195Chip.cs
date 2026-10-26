namespace Aemula.Emulation.Chips;

/// <summary>
/// Octal non-inverting buffer with tri-state outputs (81LS95). Both enables
/// must be low for the outputs to drive; if either is high all eight outputs
/// are high-impedance.
/// </summary>
public sealed class Ttl8195Chip
{
    /// <summary>
    /// Enable 1, active low.
    /// </summary>
    public bool G1 { private get; set; }

    /// <summary>
    /// Enable 2, active low.
    /// </summary>
    public bool G2 { private get; set; }

    public bool A1 { private get; set; }
    public bool? Y1 => (G1 || G2) ? null : A1;

    public bool A2 { private get; set; }
    public bool? Y2 => (G1 || G2) ? null : A2;

    public bool A3 { private get; set; }
    public bool? Y3 => (G1 || G2) ? null : A3;

    public bool A4 { private get; set; }
    public bool? Y4 => (G1 || G2) ? null : A4;

    public bool A5 { private get; set; }
    public bool? Y5 => (G1 || G2) ? null : A5;

    public bool A6 { private get; set; }
    public bool? Y6 => (G1 || G2) ? null : A6;

    public bool A7 { private get; set; }
    public bool? Y7 => (G1 || G2) ? null : A7;

    public bool A8 { private get; set; }
    public bool? Y8 => (G1 || G2) ? null : A8;
}
