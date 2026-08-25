namespace Aemula.Emulation.Chips;

/// <summary>
/// BCD-to-decimal decoder with active-low outputs. Unlike a plain 4-to-16
/// decoder (e.g. <see cref="Ttl74138Chip"/>), this only has ten outputs -
/// for the six invalid BCD codes (10-15), every output stays inactive
/// (high), which falls out naturally below since none of <see cref="Y0"/>
/// through <see cref="Y9"/>'s patterns match any value above 9.
/// </summary>
public sealed class Ttl7442Chip
{
    public bool A { private get; set; }
    public bool B { private get; set; }
    public bool C { private get; set; }
    public bool D { private get; set; }

    public bool Y0 => !(!A && !B && !C && !D);
    public bool Y1 => !(A && !B && !C && !D);
    public bool Y2 => !(!A && B && !C && !D);
    public bool Y3 => !(A && B && !C && !D);
    public bool Y4 => !(!A && !B && C && !D);
    public bool Y5 => !(A && !B && C && !D);
    public bool Y6 => !(!A && B && C && !D);
    public bool Y7 => !(A && B && C && !D);
    public bool Y8 => !(!A && !B && !C && D);
    public bool Y9 => !(A && !B && !C && D);
}
