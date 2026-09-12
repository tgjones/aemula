namespace Aemula.Emulation.Chips;

/// <summary>
/// Hex inverter with open-collector outputs. Unlike a push-pull inverter
/// (<see cref="Ttl7404Chip"/>), each output only ever actively pulls low or
/// floats - it never drives high - so a low from any one of several
/// open-collector outputs wired-OR together onto the same net always wins
/// over anything else on that net trying to hold it high.
/// </summary>
public sealed class Ttl7405Chip
{
    public bool A1 { private get; set; }
    public bool? Y1 => A1 ? false : null;

    public bool A2 { private get; set; }
    public bool? Y2 => A2 ? false : null;

    public bool A3 { private get; set; }
    public bool? Y3 => A3 ? false : null;

    public bool A4 { private get; set; }
    public bool? Y4 => A4 ? false : null;

    public bool A5 { private get; set; }
    public bool? Y5 => A5 ? false : null;

    public bool A6 { private get; set; }
    public bool? Y6 => A6 ? false : null;
}
