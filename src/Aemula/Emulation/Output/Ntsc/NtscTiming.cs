namespace Aemula.Emulation.Output.Ntsc;

// Shared nominal timing constants every Ntsc* class in this decoder starts
// from - see docs/television-plan.md's "Input signal contract". These are
// only *starting points*/search centers for self-calibrating estimates
// elsewhere in this decoder (NtscSyncSeparator's HSYNC width tracking,
// NtscRasterOscillators' horizontal/vertical period tracking) - nothing in
// this decoder hardcodes an assumption that real signals match these
// exactly.
internal static class NtscTiming
{
    // 4x the NTSC color subcarrier (3.579545MHz) - every Decode() caller in
    // this codebase samples at exactly this rate (see Television.Decode),
    // which is what makes 4-samples-per-subcarrier-cycle math (the color
    // burst PLL, YIQ demodulation, in later phases) simple.
    public const double SamplesPerSecond = 14_318_180;

    // A normal HSYNC pulse is ~4.7µs.
    public const double NominalHSyncWidthSamples = 4.7e-6 * SamplesPerSecond; // ~67.3 samples

    // 63.5µs per scanline (15.734kHz).
    public const double NominalSamplesPerLine = 63.5e-6 * SamplesPerSecond; // ~909.3 samples

    // NTSC's vertical sync pulse recurs once per *field*, not once per full
    // (2-field, interlaced) frame - 262.5 lines, not 525. That single
    // nominal value covers both a non-interlaced 262-line source (Apple II)
    // and a genuinely interlaced 525-line/2-field source (smpte.ntsc)
    // without this decoder needing to know which kind of source it's
    // looking at - see docs/television-plan.md's "Raster oscillators"
    // section.
    public const double NominalLinesPerField = 262.5;

    public const double NominalSamplesPerField = NominalLinesPerField * NominalSamplesPerLine; // ~238,691 samples

    // Color burst timing, measured from the HSYNC trailing edge (i.e. from
    // NtscRasterOscillators.CurrentColumn == 0, which is exactly where
    // NtscSyncSeparator fires HSyncDetected - the very start of back
    // porch): a 0.6µs "breezeway" gap, then the burst itself, 8-11 cycles
    // (nominally 9 - see NtscSyncSeparator's remarks on where that number
    // comes from) at exactly 4 samples/cycle, since every sample in this
    // decoder is locked to 4x the subcarrier. Unlike line/field length,
    // this window's position is *not* self-calibrated - color burst is far
    // too short and low-amplitude for the kind of pulse-width measurement
    // NtscSyncSeparator/NtscRasterOscillators do, so NtscColorBurstPll
    // starts from this fixed, spec-derived window and instead self-
    // calibrates the burst's *phase* within it (see that class).
    public const double BurstWindowStartSamples = 0.6e-6 * SamplesPerSecond; // ~8.6 samples
    public const int BurstCycleCount = 9;
    public const double BurstWindowLengthSamples = BurstCycleCount * 4; // 36 samples
}
