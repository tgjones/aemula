namespace Aemula.Emulation.Chips;

/// <summary>
/// Quad 2-input NAND buffer with open-collector outputs. Like
/// <see cref="Ttl7405Chip"/>, each output only ever pulls low or floats
/// (<c>null</c>), so several can be wired-OR'd onto one net.
/// </summary>
public sealed class Ttl7438Chip
{
    public bool A1 { private get; set; }
    public bool B1 { private get; set; }
    public bool? Y1 => A1 && B1 ? false : null;

    public bool A2 { private get; set; }
    public bool B2 { private get; set; }
    public bool? Y2 => A2 && B2 ? false : null;

    public bool A3 { private get; set; }
    public bool B3 { private get; set; }
    public bool? Y3 => A3 && B3 ? false : null;

    public bool A4 { private get; set; }
    public bool B4 { private get; set; }
    public bool? Y4 => A4 && B4 ? false : null;
}
