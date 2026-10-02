namespace Aemula.Emulation.Output.Composite;

// The timing every stage in this decoder starts from, resolved to the sample
// rate the decoder is fed at. These are only *starting points*/search centers
// for self-calibrating estimates elsewhere in this decoder (SyncSeparator's
// HSYNC width tracking, RasterOscillators' horizontal/vertical period
// tracking) - nothing in this decoder hardcodes an assumption that real
// signals match these exactly.
public sealed class TelevisionTiming
{
    // 4x the NTSC color subcarrier (3.579545MHz) - every color-capable Decode()
    // caller in this codebase samples at exactly this rate (see
    // Television.Decode), which is what makes 4-samples-per-subcarrier-cycle
    // math (the color burst PLL, YIQ demodulation) simple.
    private const float NtscSamplesPerSecond = 14_318_180;

    public static TelevisionTiming Ntsc { get; } = new(NtscSamplesPerSecond);

    private TelevisionTiming(float samplesPerSecond)
    {
        SamplesPerSecond = samplesPerSecond;

        // A normal HSYNC pulse is ~4.7µs.
        HSyncWidthSamples = 4.7e-6f * samplesPerSecond; // ~67.3 samples at 4x NTSC fsc

        // 63.5µs per scanline (15.734kHz).
        SamplesPerLine = 63.5e-6f * samplesPerSecond; // ~909.3 samples

        // NTSC's vertical sync pulse recurs once per *field*, not once per
        // full (2-field, interlaced) frame - 262.5 lines, not 525. That
        // single nominal value covers both a non-interlaced 262-line source
        // (Apple II) and a genuinely interlaced 525-line/2-field source
        // (smpte.ntsc) without this decoder needing to know which kind of
        // source it's looking at.
        LinesPerField = 262.5f;

        SamplesPerField = LinesPerField * SamplesPerLine; // ~238,691 samples

        // Color burst timing, measured from the HSYNC trailing edge (i.e.
        // from RasterOscillators.CurrentColumn == 0, which is exactly where
        // SyncSeparator fires HSyncDetected - the very start of back porch):
        // a 0.6µs "breezeway" gap, then the burst itself, 8-11 cycles
        // (nominally 9 - see SyncSeparator's remarks on where that number
        // comes from) at exactly 4 samples/cycle, since every sample in this
        // decoder is locked to 4x the subcarrier. Unlike line/field length,
        // this window's position is *not* self-calibrated - color burst is
        // far too short and low-amplitude for the kind of pulse-width
        // measurement SyncSeparator/RasterOscillators do, so ColorBurstPll
        // starts from this fixed, spec-derived window and instead self-
        // calibrates the burst's *phase* within it (see that class).
        BurstWindowStartSamples = 0.6e-6f * samplesPerSecond; // ~8.6 samples
        BurstCycleCount = 9;
        BurstWindowLengthSamples = BurstCycleCount * 4; // 36 samples

        // Front porch's share of a nominal line (1.5µs of 63.5µs) - the one
        // fixed proportion Television.ActiveVideoLengthSamples needs (nothing
        // distinguishes front porch from active video by signal content, the
        // same reason burst's own window position isn't self-calibrated - see
        // ColorBurstPll's remarks), applied to the *detected* line length
        // rather than kept as a hardcoded absolute sample count, so it still
        // scales if a real signal's line length differs from nominal (as
        // Apple II's 912-vs-909.3 already does).
        FrontPorchFraction = 1.5e-6f * samplesPerSecond / SamplesPerLine;
    }

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
