namespace Aemula.Emulation.Output;

// One decoded composite-video sample's worth of Television output - not
// just the RGB color a viewer would see, but the diagnostic context the
// decode pipeline actually used to produce it. Region (see RasterRegion)
// is the first field that needs this - docs/television-plan.md's Phase 7
// wanted TelevisionWindow's region overlays driven by what the pipeline
// really decided for each sample (NtscSyncSeparator's own live pulse-width
// classification, NtscColorBurstPll's own live burst-window flag), not a
// separate reconstruction from nominal timing that could quietly disagree
// with the real decode. A plain struct (not a class) since SampleBuffer
// below holds one of these per raster position - the same reasoning
// RgbaByte is a struct.
//
// Deliberately grown incrementally, one real field at a time, rather than
// speculatively - Color and Region are the two fields something in this
// codebase actually needs today; a future field (e.g. the raw analog level
// a sample was decoded from) gets added here when something needs *that*,
// not guessed at now.
public struct Sample
{
    public RgbaByte Color;
    public RasterRegion Region;

    // Diagnostic context for TelevisionWindow's per-sample hover tooltip
    // (docs/television-plan.md's Phase 7) - the raw composite byte
    // Television.Decode was given for this exact raster position, the
    // color-burst PLL's resolved local-oscillator phase at the moment it
    // decoded that byte (see NtscColorBurstPll.CurrentPhaseRadians), and the
    // Luma/I/Q components NtscYiqDecoder derived from it. None of these feed
    // Color/Region themselves (that decode already happened by the time
    // these are stored) - they exist purely so a historical sample's hover
    // tooltip can show *how* Color was arrived at, reading back neighboring
    // SampleBuffer entries as a de facto rolling log of the raw signal
    // (consecutive raster positions are consecutive Decode calls) rather
    // than TelevisionWindow needing its own separate capture buffer.
    public byte RawSample;
    public float CarrierPhaseRadians;
    public float Luma;
    public float I;
    public float Q;
}
