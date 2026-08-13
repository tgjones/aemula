namespace Aemula.Emulation.Chips;

/// <summary>
/// 4-bit binary full adder.
/// </summary>
public sealed class Ttl74283Chip
{
    public bool A1 { private get; set; }
    public bool A2 { private get; set; }
    public bool A3 { private get; set; }
    public bool A4 { private get; set; }

    public bool B1 { private get; set; }
    public bool B2 { private get; set; }
    public bool B3 { private get; set; }
    public bool B4 { private get; set; }

    public bool C0 { private get; set; }

    private int Sum =>
        (A1 ? 1 : 0) + (A2 ? 2 : 0) + (A3 ? 4 : 0) + (A4 ? 8 : 0) +
        (B1 ? 1 : 0) + (B2 ? 2 : 0) + (B3 ? 4 : 0) + (B4 ? 8 : 0) +
        (C0 ? 1 : 0);

    public bool S1 => (Sum & 1) != 0;
    public bool S2 => (Sum & 2) != 0;
    public bool S3 => (Sum & 4) != 0;
    public bool S4 => (Sum & 8) != 0;

    /// <summary>
    /// Carry out.
    /// </summary>
    public bool C4 => (Sum & 16) != 0;
}
