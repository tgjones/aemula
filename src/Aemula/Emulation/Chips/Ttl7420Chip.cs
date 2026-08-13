namespace Aemula.Emulation.Chips;

/// <summary>
/// Dual 4-input NAND gate.
/// </summary>
public sealed class Ttl7420Chip
{
    public bool A1 { private get; set; }
    public bool B1 { private get; set; }
    public bool C1 { private get; set; }
    public bool D1 { private get; set; }
    public bool Y1 => !(A1 && B1 && C1 && D1);

    public bool A2 { private get; set; }
    public bool B2 { private get; set; }
    public bool C2 { private get; set; }
    public bool D2 { private get; set; }
    public bool Y2 => !(A2 && B2 && C2 && D2);
}
