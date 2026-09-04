namespace Aemula.Emulation.Chips;

/// <summary>
/// Triple 3-input NAND gate.
/// </summary>
public sealed class Ttl7410Chip
{
    public bool A1 { private get; set; }
    public bool B1 { private get; set; }
    public bool C1 { private get; set; }
    public bool Y1 => !(A1 && B1 && C1);

    public bool A2 { private get; set; }
    public bool B2 { private get; set; }
    public bool C2 { private get; set; }
    public bool Y2 => !(A2 && B2 && C2);

    public bool A3 { private get; set; }
    public bool B3 { private get; set; }
    public bool C3 { private get; set; }
    public bool Y3 => !(A3 && B3 && C3);
}
