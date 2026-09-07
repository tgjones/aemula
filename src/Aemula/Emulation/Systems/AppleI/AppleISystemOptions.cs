namespace Aemula.Emulation.Systems.AppleI;

/// <summary>
/// Construction-time options for <see cref="AppleISystem"/>.
/// </summary>
public readonly struct AppleISystemOptions
{
    /// <summary>
    /// The default configuration: a bare board with nothing in the expansion
    /// connector, matching the plan's target machine.
    /// </summary>
    public static readonly AppleISystemOptions Default = new(cassetteCard: false);

    /// <summary>
    /// Whether the Apple Cassette Interface card is plugged into the expansion
    /// connector. When <see langword="true"/> its firmware answers at
    /// $C100-$C1FF and the cassette-in/out jacks become available.
    /// </summary>
    public readonly bool CassetteCard;

    public AppleISystemOptions(bool cassetteCard)
    {
        CassetteCard = cassetteCard;
    }
}
