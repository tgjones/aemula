using Aemula.Emulation.Systems.AppleI.Cassette;

namespace Aemula.Emulation.Systems.AppleI;

// The optional Apple Cassette Interface card. Fitted through the "expansion"
// slot (see AppleIExpansionSlots); when present it also sits in _expansionCard
// and is driven on the bus through the generic IExpansionCard hook in
// AppleISystem.DoCpuMemoryAccess. The card decodes $C000-$C1FF and squares up
// tape audio into a logic level; the tape itself - transport, counter, the WAV
// - lives in a CassetteDeck peripheral that the card's binding declares and the
// rig assembler patches to its two audio jacks. This partial just keeps the
// card's concrete type to hand, alongside the IExpansionCard reference the bus
// hook uses.
public sealed partial class AppleISystem
{
    private readonly AppleCassetteInterfaceCard? _cassetteCard;

    /// <summary>Whether this machine was built with the cassette interface card fitted.</summary>
    public bool HasCassetteInterface => _cassetteCard != null;

    /// <summary>The fitted ACI card, or null when the expansion connector is empty.</summary>
    public AppleCassetteInterfaceCard? CassetteInterface => _cassetteCard;
}
