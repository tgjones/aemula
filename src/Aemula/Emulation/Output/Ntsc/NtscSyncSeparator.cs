namespace Aemula.Emulation.Output.Ntsc;

// Phase 1 of docs/television-plan.md.
//
// A composite video signal carries picture, sync, and (during a short
// window each line) a color reference burst all mixed together as one
// analog waveform. Before any of that can be decoded into pixels, a real TV
// first has to figure out, sample by sample, three things:
//
//   1. Where's "sync tip" (the deepest, darkest part of the signal - used
//      for nothing but timing, never shown on screen)?
//   2. Where's "black" (the darkest part of the *picture*, as opposed to
//      sync)?
//   3. Where's "white" (the brightest part of the picture)?
//
// A TV can't just hardcode these as fixed voltages, because real signals
// drift - cable loss, source quirks (see docs/television-plan.md's "Voltage
// levels" section for how far off the real Apple II's own output runs from
// the official spec), temperature, etc. Real sets solve this with an AGC
// (automatic gain control) and a "clamp" circuit that continuously re-
// measure these three reference points from the signal itself. This class
// is the software equivalent: three self-calibrating running estimates,
// updated every sample.
//
// Once it knows roughly where "sync tip" is, it can also tell picture from
// sync moment-to-moment (anything close to the sync tip level is sync, not
// picture) and, by timing how long the signal stays down there, tell a
// normal per-line HSYNC pulse (~4.7µs) from the much broader, much longer
// VSYNC pulses that appear a handful of times per frame during vertical
// blanking (~27µs) - that's the "sync separation" half of this class.
public sealed class NtscSyncSeparator
{
    // A completed low run only counts as "a normal HSYNC pulse" if its
    // width is within this fraction of the current running HSYNC-width
    // estimate. The lower bound also keeps this class from being fooled by
    // the shorter equalizing pulses (~2.3µs, roughly half an HSYNC pulse)
    // that bracket real vertical blanking - those fall below
    // HSyncToleranceLowerFraction and are simply ignored (Phase 1 doesn't
    // need to specifically recognize them, only to not misclassify them).
    private const double HSyncToleranceLowerFraction = 0.5;
    private const double HSyncToleranceUpperFraction = 1.5;

    // A real vertical sync (broad) pulse is roughly 27.1µs - about 5.8x a
    // normal ~4.7µs HSYNC pulse - so "way longer than a normal HSYNC pulse"
    // is measured as a multiple of the current HSYNC-width estimate rather
    // than a fixed sample count. This is what lets the same logic work
    // whether a line is 910 samples (smpte.ntsc) or 912 samples (Apple II) -
    // see docs/television-plan.md's "Raster oscillators" section.
    private const double VSyncWidthMultiplier = 3.0;

    // How fast the sync-tip and white-peak trackers creep back toward
    // recent samples after snapping to a new extreme (see Process below).
    // How fast the HSYNC-width and black-level estimates smooth toward a
    // newly classified pulse. All four are free parameters with no single
    // "correct" value from first principles - see docs/television-plan.md's
    // Open risks; expect to tune these once real signals are decoding.
    private const double LevelDecayRate = 0.0005;
    private const double HSyncWidthSmoothingRate = 0.1;
    private const double BlackLevelSmoothingRate = 0.05;

    // Seeded from the Apple II's own measured levels (see the plan doc's
    // "Voltage levels" section) so decoding is sane from sample 1, without
    // waiting for the running estimates to converge: sync tip = byte 0,
    // black ≈ byte 64 (0.5V on Apple II's own 0V-2.0V scale), white = byte
    // 255. These are just a starting guess, not a hard assumption - real
    // incoming samples immediately start pulling all three estimates
    // wherever they actually belong.
    private const double InitialSyncLevel = 0;
    private const double InitialBlackLevel = 64;
    private const double InitialWhiteLevel = 255;

    private double _syncLevel = InitialSyncLevel;
    private double _blackLevel = InitialBlackLevel;
    private double _whiteLevel = InitialWhiteLevel;
    private double _hsyncWidthEstimate = NtscTiming.NominalHSyncWidthSamples;

    // How many consecutive samples we've been below sync level for, in the
    // pulse currently in progress (0 when not currently in a low pulse).
    private int _lowRunLength;

    /// <summary>
    /// The running estimate of the sync tip voltage, on the same 0-255 byte
    /// scale <see cref="Television.Decode"/> receives samples on.
    /// </summary>
    public double SyncLevel => _syncLevel;

    /// <summary>
    /// The running estimate of the black (picture-minimum) level.
    /// </summary>
    public double BlackLevel => _blackLevel;

    /// <summary>
    /// The running estimate of the white (picture-maximum) level.
    /// </summary>
    public double WhiteLevel => _whiteLevel;

    /// <summary>
    /// True only for the single <see cref="Process"/> call in which a
    /// normal-width (HSYNC-like) low pulse's trailing edge was found - an
    /// edge-triggered pulse, not a level.
    /// </summary>
    public bool HSyncDetected { get; private set; }

    /// <summary>
    /// True only for the single <see cref="Process"/> call in which a
    /// much-longer-than-normal (VSYNC-like) low pulse's trailing edge was
    /// found - an edge-triggered pulse, not a level.
    /// </summary>
    public bool VSyncDetected { get; private set; }

    /// <summary>
    /// Whether the most recently processed sample was itself below sync
    /// level (i.e. is part of a sync pulse, not picture).
    /// </summary>
    public bool IsBelowSyncLevel { get; private set; }

    /// <summary>
    /// Classifies a sample against the *current* running sync/black
    /// estimates, without mutating any state - the threshold sits at the
    /// midpoint between the two, i.e. a sample counts as "sync" once it's
    /// closer to the sync tip estimate than to the black-level estimate.
    /// </summary>
    public bool ClassifyBelowSyncLevel(byte sample) => sample < (_syncLevel + _blackLevel) / 2.0;

    /// <summary>
    /// Feeds one composite-video sample into the separator. Call this once
    /// per sample, in order - the running level estimates and HSYNC/VSYNC
    /// detection both depend on seeing every sample, not just the
    /// interesting ones.
    /// </summary>
    public void Process(byte sample)
    {
        HSyncDetected = false;
        VSyncDetected = false;

        var isBelowSyncLevel = ClassifyBelowSyncLevel(sample);

        if (isBelowSyncLevel)
        {
            _lowRunLength++;

            // Fast-attack, slow-decay running minimum: a real clamp circuit
            // re-clamps to sync tip every single line, so this should snap
            // down immediately the moment a lower sample appears, but only
            // creep back up slowly if the true sync tip level drifts higher
            // over time (rather than one noisy low sample permanently
            // dragging the estimate down).
            if (sample < _syncLevel)
            {
                _syncLevel = sample;
            }
            else
            {
                _syncLevel += (sample - _syncLevel) * LevelDecayRate;
            }
        }
        else
        {
            // Same fast-attack, slow-decay idea, mirrored to track a
            // running *maximum* instead of minimum - approximates "peak of
            // active video" (the plan doc's AGC white-level reference)
            // without needing to know precisely where active video starts
            // and ends (that's the raster oscillators' job, Phase 2): non-
            // sync samples are dominated by genuine active-video whites
            // simply because active video occupies most (~83%) of a
            // scanline's duration, so the observed peak among non-sync
            // samples is realistically going to land on real picture
            // content.
            if (sample > _whiteLevel)
            {
                _whiteLevel = sample;
            }
            else
            {
                _whiteLevel -= (_whiteLevel - sample) * LevelDecayRate;
            }

            if (_lowRunLength > 0)
            {
                ClassifyCompletedLowRun(_lowRunLength, sample);
                _lowRunLength = 0;
            }
        }

        IsBelowSyncLevel = isBelowSyncLevel;
    }

    // A low run (a run of consecutive below-sync-level samples) just ended
    // on the sample being processed right now - sampleAfterPulse is that
    // sample, i.e. literally "the sample immediately following the pulse's
    // trailing edge".
    private void ClassifyCompletedLowRun(int runLength, byte sampleAfterPulse)
    {
        var hsyncLowerBound = _hsyncWidthEstimate * HSyncToleranceLowerFraction;
        var hsyncUpperBound = _hsyncWidthEstimate * HSyncToleranceUpperFraction;
        var vsyncThreshold = _hsyncWidthEstimate * VSyncWidthMultiplier;

        if (runLength >= hsyncLowerBound && runLength <= hsyncUpperBound)
        {
            _hsyncWidthEstimate += (runLength - _hsyncWidthEstimate) * HSyncWidthSmoothingRate;

            // The sample right after HSYNC's trailing edge is the back
            // porch - after sync, before color burst - which is exactly
            // the "0 IRE" reference point a real decoder's clamp pulse
            // samples to re-establish black level once per line.
            _blackLevel += (sampleAfterPulse - _blackLevel) * BlackLevelSmoothingRate;

            HSyncDetected = true;
        }
        else if (runLength >= vsyncThreshold)
        {
            VSyncDetected = true;
        }
        // Otherwise: too short to be a normal HSYNC pulse, not long enough
        // to be a VSYNC pulse either - most likely a vertical-blanking
        // equalizing pulse. Phase 1 doesn't need to specifically recognize
        // these, only to avoid misclassifying them as one of the other two.
    }
}
