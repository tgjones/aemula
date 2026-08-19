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

    // Where active (visible-picture) video starts and how long it lasts,
    // both measured from the same HSYNC-trailing-edge zero point as the
    // burst window above. The classic RS-170A breakdown of the ~4.7us
    // between HSYNC's trailing edge and active video's start is: 0.6us
    // breezeway (where BurstWindowStartSamples already begins), 2.5us burst
    // itself (9 cycles - already captured as BurstWindowLengthSamples above,
    // and 0.6+2.5=3.1us, comfortably inside the 4.7us total), then a further
    // 1.6us of plain back porch before the picture starts - see the Raster
    // Graphics Handbook (RS170.pdf) and NTSC Studio Timing PDF, both already
    // linked from this project's own README.md. Active video itself then
    // runs 52.6us, with the remaining 1.5us of the 63.5us line being front
    // porch (4.7 + 4.7 + 52.6 + 1.5 = 63.5, matching NominalSamplesPerLine).
    public const double ActiveVideoStartSamples = 4.7e-6 * SamplesPerSecond; // ~67.3 samples
    public const double ActiveVideoLengthSamples = 52.6e-6 * SamplesPerSecond; // ~753.1 samples
}
