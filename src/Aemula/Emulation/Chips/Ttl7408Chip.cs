namespace Aemula.Emulation.Chips;

/// <summary>
/// Quad 2-input AND gate.
/// </summary>
public sealed class Ttl7408Chip
{
    public bool A1 { private get; set; }
    public bool B1 { private get; set; }
    public bool Y1 => A1 && B1;

    public bool A2 { private get; set; }
    public bool B2 { private get; set; }
    public bool Y2 => A2 && B2;

    public bool A3 { private get; set; }
    public bool B3 { private get; set; }
    public bool Y3 => A3 && B3;

    public bool A4 { private get; set; }
    public bool B4 { private get; set; }
    public bool Y4 => A4 && B4;
}
