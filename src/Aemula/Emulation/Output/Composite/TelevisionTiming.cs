namespace Aemula.Emulation.Output.Composite;

// A TelevisionStandard's timing resolved to the sample rate the decoder is
// fed at. These are only *starting points*/search centers for
// self-calibrating estimates elsewhere in this decoder (SyncSeparator's
// HSYNC width tracking, RasterOscillators' horizontal/vertical period
// tracking) - nothing in this decoder hardcodes an assumption that real
// signals match these exactly.
//
// The sample rate defaults to 4x the standard's color subcarrier, which is
// what a color-capable producer must use (see TelevisionStandard.
// SamplesPerSecond). A monochrome producer can pass its own native rate
// instead: nothing here assumes the two are related except the burst window's
// length, which is only meaningful at 4 samples per cycle and which a signal
// with no burst never exercises.
public sealed class TelevisionTiming
{
    public static TelevisionTiming Ntsc { get; } = new(TelevisionStandard.Ntsc);

    public TelevisionTiming(TelevisionStandard standard, float? samplesPerSecond = null)
    {
        Standard = standard;
        var rate = samplesPerSecond ?? standard.SamplesPerSecond;
        SamplesPerSecond = rate;

        HSyncWidthSamples = standard.HSyncWidthSeconds * rate;
        SamplesPerLine = standard.LineSeconds * rate;
        LinesPerField = standard.LinesPerField;
        SamplesPerField = LinesPerField * SamplesPerLine;

        // Color burst timing, measured from the HSYNC trailing edge (i.e.
        // from RasterOscillators.CurrentColumn == 0, which is exactly where
        // SyncSeparator fires HSyncDetected - the very start of back porch):
        // a short "breezeway" gap, then the burst itself - a handful of
        // cycles (NTSC 8-11, nominally 9 - see SyncSeparator's remarks on
        // where that number comes from; PAL 10). Unlike line/field length,
        // this window's position is *not* self-calibrated - color burst is
        // far too short and low-amplitude for the kind of pulse-width
        // measurement SyncSeparator/RasterOscillators do, so ColorBurstPll
        // starts from this fixed, spec-derived window and instead self-
        // calibrates the burst's *phase* within it (see that class).
        BurstWindowStartSamples = standard.BurstStartSeconds * rate;
        BurstCycleCount = standard.BurstCycles;
        BurstWindowLengthSamples = (float)(BurstCycleCount * rate / standard.ColorSubcarrierHz);

        // Front porch's share of a nominal line - the one fixed proportion
        // Television.ActiveVideoLengthSamples needs (nothing distinguishes
        // front porch from active video by signal content, the same reason
        // burst's own window position isn't self-calibrated - see
        // ColorBurstPll's remarks), applied to the *detected* line length
        // rather than kept as a hardcoded absolute sample count, so it still
        // scales if a real signal's line length differs from nominal (as
        // Apple II's 912-vs-909.3 already does).
        FrontPorchFraction = standard.FrontPorchSeconds * rate / SamplesPerLine;
    }

    public TelevisionStandard Standard { get; }
    public float SamplesPerSecond { get; }
    public float HSyncWidthSamples { get; }
    public float SamplesPerLine { get; }
    public float LinesPerField { get; }
    public float SamplesPerField { get; }
    public float BurstWindowStartSamples { get; }
    public int BurstCycleCount { get; }
    public float BurstWindowLengthSamples { get; }
    public float FrontPorchFraction { get; }
}
