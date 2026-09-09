using Aemula.Emulation.Systems.AppleI.Cassette;

namespace Aemula.Emulation.Systems.AppleI;

// The optional Apple Cassette Interface card. The card decodes $C000-$C1FF off
// the expansion connector and squares up tape audio into a logic level; it is
// wired to the CPU bus through the generic IExpansionCard hook in
// AppleISystem.DoCpuMemoryAccess. The tape itself - transport, counter, the
// WAV - lives in a CassetteDeck peripheral that the rig assembler patches to
// the card's two audio jacks (AppleCassetteInterfaceCard.CassetteInput /
// CassetteOutput). This partial just owns the typed reference.
public sealed partial class AppleISystem
{
    private readonly AppleCassetteInterfaceCard? _cassetteCard;

    /// <summary>Whether this machine was built with the cassette interface card.</summary>
    public bool HasCassetteInterface => _cassetteCard != null;

    /// <summary>
    /// The fitted ACI card, or null on a bare board. Exposed for the rig
    /// assembler to patch its audio jacks to a <c>CassetteDeck</c>.
    /// </summary>
    public AppleCassetteInterfaceCard? CassetteInterface => _cassetteCard;
}
