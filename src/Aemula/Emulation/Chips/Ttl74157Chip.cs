namespace Aemula.Emulation.Chips;

/// <summary>
/// Quad 2-input multiplexer, non-tri-state, with a single select line and a
/// single active-low strobe shared across all four channels. Unlike
/// <see cref="Ttl74257Chip"/> (the tri-state quad 2:1 mux already in this
/// repo), a disabled <see cref="Ttl74157Chip"/> drives every output low
/// rather than going high-impedance - same disabled behavior as
/// <see cref="Ttl74153Chip"/>'s per-unit strobes.
/// </summary>
public sealed class Ttl74157Chip
{
    public bool S { private get; set; }

    /// <summary>
    /// Strobe, active low. Must be asserted for the outputs to reflect the
    /// selected inputs; otherwise every output is forced low.
    /// </summary>
    public bool G { private get; set; }

    public bool A1 { private get; set; }
    public bool B1 { private get; set; }
    public bool Y1 => !G && (S ? B1 : A1);

    public bool A2 { private get; set; }
    public bool B2 { private get; set; }
    public bool Y2 => !G && (S ? B2 : A2);

    public bool A3 { private get; set; }
    public bool B3 { private get; set; }
    public bool Y3 => !G && (S ? B3 : A3);

    public bool A4 { private get; set; }
    public bool B4 { private get; set; }
    public bool Y4 => !G && (S ? B4 : A4);
}
