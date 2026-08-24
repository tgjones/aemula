namespace Aemula.Emulation.Output;

// TelevisionWindow's Saleae-style overlay nicety needs a name for "what part
// of the signal produced this sample", the same way a logic analyzer names
// the regions of a waveform
// it's showing you. Television.Decode determines this live, per sample, from
// state the decode pipeline's own earlier stages already computed for their
// own reasons, and stores the result in that sample's Sample.Region, rather
// than TelevisionWindow (or anything else) re-deriving it after the fact
// from nominal timing - a from-nominal-timing version of this existed
// briefly and was deliberately replaced; see
// Ntsc.NtscSyncSeparator.CurrentSyncRegion's remarks for why "live, from the
// same state the rest of the pipeline already uses" matters.
//
// Lives here, one level up from the Ntsc/ classes that currently populate it
// (rather than under Ntsc/ itself, unlike almost everything else this
// decoder needed), because - unlike the sample-rate assumptions, the YIQ
// matrix, or the burst PLL's phase stepping - a sync pulse, a color-burst
// reference, blanking, and
// active picture aren't NTSC-specific concepts: PAL has all four too, with
// its own timing but the same names. Nothing about that claim is NTSC-vs-PAL
// speculative the way a shared base class between their decode pipelines
// would be - it's just what these regions are called.
public enum RasterRegion
{
    /// <summary>
    /// The visible picture - what a real TV's screen actually shows.
    /// </summary>
    ActiveVideo,

    /// <summary>
    /// The short, sharp pulse a real TV's horizontal oscillator locks onto to
    /// know when each scanline starts.
    /// </summary>
    HSync,

    /// <summary>
    /// The short reference burst of color subcarrier a receiver locks its
    /// chroma-demodulator phase to, sent once near the start of every line.
    /// </summary>
    ColorBurst,

    /// <summary>
    /// Everything else that isn't picture, HSYNC, color burst, or VSYNC - the
    /// breezeway/back porch/front porch "dead time" every line carries around
    /// its sync pulse.
    /// </summary>
    Blanking,

    /// <summary>
    /// The broader, longer pulses sent a handful of times per field, near the
    /// top of it, that a real TV's vertical oscillator locks onto to know
    /// when each field starts.
    /// </summary>
    VSync,
}
