namespace Aemula.Emulation.Chips;

/// <summary>
/// Hex inverter.
/// </summary>
public sealed class Ttl7404Chip
{
    public bool A1 { private get; set; }
    public bool Y1 => !A1;

    public bool A2 { private get; set; }
    public bool Y2 => !A2;

    public bool A3 { private get; set; }
    public bool Y3 => !A3;

    public bool A4 { private get; set; }
    public bool Y4 => !A4;

    public bool A5 { private get; set; }
    public bool Y5 => !A5;

    public bool A6 { private get; set; }
    public bool Y6 => !A6;
}
