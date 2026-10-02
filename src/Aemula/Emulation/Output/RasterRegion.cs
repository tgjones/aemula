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
// SyncSeparator.CurrentSyncRegion's remarks for why "live, from the
// same state the rest of the pipeline already uses" matters.
//
// These are the same four things on every standard - NTSC and PAL both have a
// sync pulse, a color-burst reference, blanking and active picture, with
// their own timing but the same names - so the enum is not specific to any
// one standard's decoder.
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
