namespace Aemula.Emulation.Chips;

/// <summary>
/// Octal bus transceiver with tri-state outputs. Each pin is bidirectional, so
/// each side has an input property (the level some other chip is driving onto
/// the pin, <c>null</c> if nothing is) and an <c>...Out</c> property (the level
/// this chip drives, <c>null</c> for high-impedance). With <see cref="G"/>
/// low, <see cref="Dir"/> high passes A to B and low passes B to A; with
/// <see cref="G"/> high both sides float.
/// </summary>
public sealed class Ttl74245Chip
{
    /// <summary>
    /// Enable, active low. High makes every output high-impedance.
    /// </summary>
    public bool G { private get; set; }

    /// <summary>
    /// Direction. High: A to B. Low: B to A.
    /// </summary>
    public bool Dir { private get; set; }

    public bool? A0 { private get; set; }
    public bool? A0Out => G || Dir ? null : B0;

    public bool? B0 { private get; set; }
    public bool? B0Out => G || !Dir ? null : A0;

    public bool? A1 { private get; set; }
    public bool? A1Out => G || Dir ? null : B1;

    public bool? B1 { private get; set; }
    public bool? B1Out => G || !Dir ? null : A1;

    public bool? A2 { private get; set; }
    public bool? A2Out => G || Dir ? null : B2;

    public bool? B2 { private get; set; }
    public bool? B2Out => G || !Dir ? null : A2;

    public bool? A3 { private get; set; }
    public bool? A3Out => G || Dir ? null : B3;

    public bool? B3 { private get; set; }
    public bool? B3Out => G || !Dir ? null : A3;

    public bool? A4 { private get; set; }
    public bool? A4Out => G || Dir ? null : B4;

    public bool? B4 { private get; set; }
    public bool? B4Out => G || !Dir ? null : A4;

    public bool? A5 { private get; set; }
    public bool? A5Out => G || Dir ? null : B5;

    public bool? B5 { private get; set; }
    public bool? B5Out => G || !Dir ? null : A5;

    public bool? A6 { private get; set; }
    public bool? A6Out => G || Dir ? null : B6;

    public bool? B6 { private get; set; }
    public bool? B6Out => G || !Dir ? null : A6;

    public bool? A7 { private get; set; }
    public bool? A7Out => G || Dir ? null : B7;

    public bool? B7 { private get; set; }
    public bool? B7Out => G || !Dir ? null : A7;
}
