namespace Aemula.Emulation.Systems.AppleII;

/// <summary>
/// Which Apple II motherboard revision <see cref="AppleIISystem"/> models. The
/// only behavioural difference captured today is the colour-killer circuit
/// (see <see cref="AppleIISystemOptions.Revision"/>).
/// </summary>
public enum AppleIIRevision
{
    /// <summary>
    /// The original 1977 board. It has no colour-killer circuit, so colour
    /// burst leaves the machine on every scanline regardless of video mode,
    /// and 40-column text picks up green/violet composite artifact colour on
    /// a colour monitor or TV.
    /// </summary>
    Revision0,

    /// <summary>
    /// Revision 1 and every later board, up to and including the RFI
    /// revision. A colour-killer transistor gated by the TEXT soft switch
    /// pulls the colour reference away from the video-summing node whenever
    /// the machine is in full-screen text mode, so no burst goes out and a
    /// colour receiver squelches chroma to show crisp monochrome text. Mixed
    /// text/graphics keeps the TEXT switch low, so its bottom text rows still
    /// fringe with artifact colour - matching real hardware.
    /// </summary>
    Revision1Plus,
}

/// <summary>
/// Construction-time options for <see cref="AppleIISystem"/>.
/// </summary>
public readonly struct AppleIISystemOptions
{
    /// <summary>
    /// The default configuration: a Revision 1-or-later board, matching the
    /// Apple II+ the rest of the emulation targets.
    /// </summary>
    public static readonly AppleIISystemOptions Default = new(AppleIIRevision.Revision1Plus);

    /// <summary>
    /// The motherboard revision to model. Defaults to
    /// <see cref="AppleIIRevision.Revision1Plus"/>.
    /// </summary>
    public readonly AppleIIRevision Revision;

    public AppleIISystemOptions(AppleIIRevision revision)
    {
        Revision = revision;
    }
}
