namespace Aemula.Emulation.Chips;

/// <summary>
/// 2-wide 4-input AND-OR-INVERT gate: two 4-input AND terms feed a single
/// NOR, giving one output for the whole chip (unlike the 4-wide 2-input
/// <see cref="Ttl7420Chip"/>-shaped parts in this repo, which expose two
/// independent gates).
/// </summary>
public sealed class Ttl7455Chip
{
    public bool A1 { private get; set; }
    public bool B1 { private get; set; }
    public bool C1 { private get; set; }
    public bool D1 { private get; set; }

    public bool A2 { private get; set; }
    public bool B2 { private get; set; }
    public bool C2 { private get; set; }
    public bool D2 { private get; set; }

    public bool Y => !((A1 && B1 && C1 && D1) || (A2 && B2 && C2 && D2));
}
