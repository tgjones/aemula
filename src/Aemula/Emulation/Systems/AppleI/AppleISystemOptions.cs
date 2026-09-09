namespace Aemula.Emulation.Systems.AppleI;

/// <summary>
/// Construction-time options for <see cref="AppleISystem"/>.
/// </summary>
public readonly struct AppleISystemOptions
{
    /// <summary>
    /// The default configuration: a bare 8K board with nothing in the
    /// expansion connector, matching the plan's target machine.
    /// </summary>
    public static readonly AppleISystemOptions Default = new(cassetteCard: false);

    /// <summary>
    /// Whether the Apple Cassette Interface card is plugged into the expansion
    /// connector. When <see langword="true"/> its firmware answers at
    /// $C100-$C1FF and the cassette-in/out jacks become available.
    /// </summary>
    public readonly bool CassetteCard;

    /// <summary>
    /// Whether a 4K RAM expansion is jumpered into the $E000-$EFFF block (the
    /// CSE decoder output, otherwise an unpopulated expansion block). This is
    /// the RAM Apple 1 Integer BASIC loads and runs from, so it's needed to
    /// load BASIC off cassette; a bare board doesn't have it.
    /// </summary>
    public readonly bool RamExpansionAtE000;

    public AppleISystemOptions(bool cassetteCard, bool ramExpansionAtE000 = false)
    {
        CassetteCard = cassetteCard;
        RamExpansionAtE000 = ramExpansionAtE000;
    }
}
