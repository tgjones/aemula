namespace Aemula.Emulation.Systems.AppleI;

/// <summary>
/// Where the second onboard 4K DRAM bank (ICA row) is jumpered. The Apple I has
/// two 4K banks; bank A is fixed at $0000-$0FFF and bank B's jumper block maps
/// it to one of these ranges - never both, since only the two banks exist. More
/// RAM than that needed a card on the expansion connector.
/// </summary>
public enum AppleIBankBMapping
{
    /// <summary>$1000-$1FFF - contiguous 8K, the fully-populated bare board.</summary>
    At1000,

    /// <summary>
    /// $E000-$EFFF - the split configuration Integer BASIC needs (it loads and
    /// runs from $E000). $1000-$1FFF is then unpopulated.
    /// </summary>
    AtE000,
}

/// <summary>
/// Construction-time options for <see cref="AppleISystem"/>. What card sits in
/// the expansion connector is a separate concern - see the "expansion" slot in
/// <see cref="AppleIExpansionSlots"/> and the <see cref="ExpansionSlotConfiguration"/>
/// passed alongside these options.
/// </summary>
public readonly struct AppleISystemOptions
{
    /// <summary>
    /// A bare, fully-populated 8K board: both 4K banks fitted and contiguous at
    /// $0000-$1FFF.
    /// </summary>
    public static readonly AppleISystemOptions Default = new(AppleIBankBMapping.At1000);

    /// <summary>
    /// The board as an owner would jumper it to run Integer BASIC: bank B moved
    /// to $E000-$EFFF, leaving 4K at $0000 and 4K at $E000.
    /// </summary>
    public static readonly AppleISystemOptions EquippedForBasic = new(AppleIBankBMapping.AtE000);

    /// <summary>Where the second 4K DRAM bank is jumpered.</summary>
    public readonly AppleIBankBMapping BankB;

    public AppleISystemOptions(AppleIBankBMapping bankB = AppleIBankBMapping.At1000)
    {
        BankB = bankB;
    }
}
