namespace Aemula.Emulation.Output;

public sealed class Television
{
    // Hardcoded for now - see docs/television-plan.md's "Standard detection
    // seam". A real multi-standard TV works this out from the incoming
    // signal itself (line/frame rate, and PAL's line-to-line burst-phase
    // alternation), but this class doesn't have a PAL decode path to switch
    // to yet, so there's nothing to detect.
    public TelevisionStandard Standard => TelevisionStandard.Ntsc;
}
