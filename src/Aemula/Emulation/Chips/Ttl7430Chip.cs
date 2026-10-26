namespace Aemula.Emulation.Chips;

/// <summary>
/// 8-input NAND gate.
/// </summary>
public sealed class Ttl7430Chip
{
    public bool A { private get; set; }
    public bool B { private get; set; }
    public bool C { private get; set; }
    public bool D { private get; set; }
    public bool E { private get; set; }
    public bool F { private get; set; }
    public bool G { private get; set; }
    public bool H { private get; set; }
    public bool Y => !(A && B && C && D && E && F && G && H);
}
