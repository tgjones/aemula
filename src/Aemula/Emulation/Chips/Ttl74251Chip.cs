namespace Aemula.Emulation.Chips;

/// <summary>
/// 8-to-1 data selector/multiplexer with tri-state outputs.
/// </summary>
public sealed class Ttl74251Chip
{
    public bool D0 { private get; set; }
    public bool D1 { private get; set; }
    public bool D2 { private get; set; }
    public bool D3 { private get; set; }
    public bool D4 { private get; set; }
    public bool D5 { private get; set; }
    public bool D6 { private get; set; }
    public bool D7 { private get; set; }

    public bool A { private get; set; }
    public bool B { private get; set; }
    public bool C { private get; set; }

    /// <summary>
    /// Strobe, active low. Must be asserted for <see cref="Y"/> and
    /// <see cref="W"/> to be driven; otherwise both are high-impedance.
    /// </summary>
    public bool S { private get; set; }

    private bool SelectedInput => (A, B, C) switch
    {
        (false, false, false) => D0,
        (true, false, false) => D1,
        (false, true, false) => D2,
        (true, true, false) => D3,
        (false, false, true) => D4,
        (true, false, true) => D5,
        (false, true, true) => D6,
        (true, true, true) => D7,
    };

    public bool? Y => S ? null : SelectedInput;

    public bool? W => S ? null : !SelectedInput;
}
