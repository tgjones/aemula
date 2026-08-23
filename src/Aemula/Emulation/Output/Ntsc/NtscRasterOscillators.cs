using System;

namespace Aemula.Emulation.Output.Ntsc;

// Phase 2 of docs/television-plan.md.
//
// A CRT draws a picture by sweeping an electron beam left-to-right along
// each line, then jumping back and starting the next line slightly lower -
// and doing that whole thing over and over, top-to-bottom, ~60 times a
// second. The two "oscillators" here are the software model of exactly
// that: a horizontal one that free-runs once per scanline, and a vertical
// one that free-runs once per field, together tracking "where is the beam
// right now" as a (column, row) position for every incoming sample.
//
// The key thing that makes this behave like a *real* TV rather than a
// magic perfect clock: a real horizontal/vertical oscillator is a
// free-running thing with its own natural period (like a horizontal-hold
// knob's center frequency), which incoming HSYNC/VSYNC pulses only gently
// pull into phase - within a limited "capture range" - rather than being
// directly driven by them. Pulses that land outside that capture range are
// noise, not sync, and get ignored; and if no valid pulses show up for a
// while, the oscillator just keeps going on its own (a "flywheel"),
// producing a torn or rolling picture rather than freezing or crashing.
// This is what makes the whole decoder behave sensibly - not perfectly,
// but the same way a real set would - on a badly out-of-spec signal.
public sealed class NtscRasterOscillators
{
    // Horizontal capture range and smoothing: pulses within 15% of the
    // current line-length estimate are trusted; the estimate itself moves
    // 10% of the way toward each newly-accepted measurement. 15% is wide
    // enough that a bad *first* measurement (the very first pulse this
    // oscillator ever sees can land almost anywhere in a line, depending on
    // where in the stream decoding happened to start - see PullInOscillator
    // below) doesn't get stuck rejecting every genuine pulse that follows
    // while the estimate is still converging toward the truth, but still
    // comfortably rejects anything from a different NTSC-family signal
    // entirely (see the out-of-range test in NtscRasterOscillatorsTests).
    private const float HorizontalCaptureRangeFraction = 0.15f;
    private const float HorizontalSmoothingRate = 0.1f;

    // Vertical capture range and smoothing - wider tolerance and faster
    // smoothing than horizontal's, since a field boundary is only measured
    // a handful of times total in even a long capture (once per field,
    // versus once per line), so there's much less opportunity to converge
    // gradually - it needs to get close in just a few pulses.
    private const float VerticalCaptureRangeFraction = 0.2f;
    private const float VerticalSmoothingRate = 0.3f;

    // No matter how many pulses get accepted, a period estimate can never
    // drift more than this far from its nominal NTSC value - modeling a
    // real oscillator's bounded natural frequency range (it can be pulled a
    // little, not tuned anywhere). This is what guarantees a signal with
    // wildly wrong timing can never be mistaken for real sync, no matter
    // how many pulses it offers.
    private const float MaxPeriodDriftFraction = 0.2f;

    // A real vertical sync region is several HSYNC-width-or-broader pulses
    // in a row (equalizing + broad serration pulses), not one - see the
    // class remarks on NtscSyncSeparator. Any VSYNC-classified pulse
    // arriving within this many *current horizontal line lengths* of the
    // last one considered is treated as part of the same vertical-blanking
    // region, not a fresh field boundary - empirically, real vertical sync
    // regions in this codebase's two test signals span up to ~3.5 line
    // widths, so this leaves comfortable margin while staying utterly
    // negligible next to the ~262-line gap between genuine fields.
    private const float VerticalDebounceLineMultiplier = 4.0f;

    private readonly PullInOscillator _horizontal = new(
        NtscTiming.NominalSamplesPerLine,
        HorizontalCaptureRangeFraction,
        HorizontalSmoothingRate);

    private readonly PullInOscillator _vertical = new(
        NtscTiming.NominalSamplesPerField,
        VerticalCaptureRangeFraction,
        VerticalSmoothingRate);

    // How long it's been since the last VSYNC-classified pulse was even
    // considered (accepted or not) - the debounce gate described above.
    // Seeded huge so the very first VSYNC pulse in a stream is always
    // considered.
    private float _samplesSinceLastVSyncCandidate = 1e12f;

    /// <summary>
    /// The raster column (sample position within the current line) of the
    /// sample just processed.
    /// </summary>
    public int CurrentColumn => (int)_horizontal.Position;

    /// <summary>
    /// The raster row (line position within the current field) of the
    /// sample just processed.
    /// </summary>
    public int CurrentRow => (int)(_vertical.Position / _horizontal.PeriodEstimate);

    /// <summary>
    /// The current running estimate of samples-per-line, measured from real
    /// HSYNC spacing rather than configured - see the plan doc's "Raster
    /// oscillators" section.
    /// </summary>
    public float DetectedSamplesPerLine => _horizontal.PeriodEstimate;

    /// <summary>
    /// The current running estimate of lines-per-field, derived from the
    /// vertical oscillator's own (sample-based) period estimate divided by
    /// the horizontal one's - see the class remarks on why both oscillators
    /// operate in raw sample units internally.
    /// </summary>
    public float DetectedLinesPerFrame => _vertical.PeriodEstimate / _horizontal.PeriodEstimate;

    /// <summary>
    /// Advances both oscillators by one sample. <paramref name="hSyncDetected"/>
    /// and <paramref name="vSyncDetected"/> should come from the same
    /// sample's <see cref="NtscSyncSeparator.HSyncDetected"/> and
    /// <see cref="NtscSyncSeparator.VSyncDetected"/>.
    /// </summary>
    public void Process(bool hSyncDetected, bool vSyncDetected)
    {
        _horizontal.Tick(hSyncDetected);

        _samplesSinceLastVSyncCandidate += 1f;

        var offerVSync = false;
        if (vSyncDetected && _samplesSinceLastVSyncCandidate >= _horizontal.PeriodEstimate * VerticalDebounceLineMultiplier)
        {
            _samplesSinceLastVSyncCandidate = 0;
            offerVSync = true;
        }

        // The vertical oscillator advances every sample (not once per
        // line) so that a VSYNC pulse - which, per the empirical spacing in
        // both this codebase's test signals, doesn't line up with
        // horizontal line boundaries - can always be offered on the exact
        // sample it was detected on.
        _vertical.Tick(offerVSync);
    }

    // Shared pull-in/flywheel logic behind both oscillators above - one
    // small state machine bundling two pieces of real-hardware behavior:
    //
    //   1. A capture range, centered on the oscillator's own current period
    //      estimate: an offered pulse only gets trusted (and used to refine
    //      the estimate) if it's within a bounded tolerance of what the
    //      oscillator already expects. The very first pulse this oscillator
    //      ever sees is the one exception - it's accepted unconditionally,
    //      since there's no prior measurement yet to validate a spacing
    //      against (the same way real hardware can't judge a period from a
    //      single point in time). Because the *estimate* itself is bounded
    //      to a fixed band around nominal (see MaxPeriodDriftFraction), the
    //      capture range effectively can't drift arbitrarily far from
    //      nominal either, even starting from a bad first measurement.
    //   2. A flywheel free-run: Position keeps advancing every Tick
    //      regardless of whether a pulse was accepted, wrapping back to
    //      zero (and reporting a boundary crossing) either when a pulse is
    //      accepted or, with no pulse in sight, whenever a full period's
    //      worth of samples has simply gone by on its own.
    private sealed class PullInOscillator(float nominalPeriod, float captureRangeFraction, float smoothingRate)
    {
        private readonly float _nominalPeriod = nominalPeriod;

        public float Position { get; private set; }
        public float PeriodEstimate { get; private set; } = nominalPeriod;

        // Samples elapsed since the last *accepted* pulse - unlike Position
        // (above), this is never touched by a free-run wrap, only by a
        // genuine accept. That distinction matters: if PeriodEstimate is
        // even slightly under the true period, Position free-run-wraps
        // *before* the next real pulse arrives, so by the time that pulse
        // shows up, Position has already reset and no longer reflects how
        // long it's actually been. Validating pulses (and measuring their
        // true spacing) against this counter instead of Position is what
        // keeps a slightly-off estimate correcting itself back toward the
        // real period instead of spiraling toward the drift clamp - see the
        // "premature free-run wrap" bug this fixed, found while chasing
        // down why real signals weren't converging in
        // NtscRasterOscillatorsTests. Starts at 0, in step with Position -
        // it's also used as *this* accept's measured period (see Tick
        // below), so it has to genuinely reflect "samples since Tick
        // started counting" even for the very first accept, not just be a
        // large sentinel; _hasEverAccepted (not this) is what guarantees
        // that very first pulse is accepted regardless of its value.
        private float _samplesSinceAccepted;
        private bool _hasEverAccepted;

        /// <summary>
        /// Advances by one sample. <paramref name="pulseOffered"/> is
        /// whether a sync pulse was detected on this exact sample.
        /// Returns true exactly on the sample a boundary was crossed -
        /// either because a pulse was accepted, or because the oscillator
        /// free-ran past a full period with no pulse accepted.
        /// </summary>
        public bool Tick(bool pulseOffered)
        {
            Position += 1f;
            _samplesSinceAccepted += 1f;

            if (pulseOffered && IsWithinCaptureRange())
            {
                // A period is a measurement *between* two points - the very
                // first pulse this oscillator ever sees only establishes
                // where "phase zero" is, the same way a real set's
                // oscillator has to grab an arbitrary first reference point
                // before it has any interval to measure yet. Nudging
                // PeriodEstimate toward that first (essentially arbitrary,
                // depends only on where in the stream decoding happened to
                // start) Position would just be adding noise, not signal -
                // wait for the *second* accept, which is the first one with
                // a genuine measured interval behind it.
                if (_hasEverAccepted)
                {
                    var measuredPeriod = _samplesSinceAccepted;
                    var target = PeriodEstimate + (measuredPeriod - PeriodEstimate) * smoothingRate;

                    // A real oscillator can be nudged, not retuned -
                    // bounding the estimate to a fixed band around nominal
                    // is what guarantees a wildly-wrong pulse train can
                    // never be mistaken for genuine sync, no matter how
                    // many times it's (partially) accepted - see
                    // MaxPeriodDriftFraction above.
                    PeriodEstimate = Math.Clamp(
                        target,
                        _nominalPeriod * (1 - MaxPeriodDriftFraction),
                        _nominalPeriod * (1 + MaxPeriodDriftFraction));
                }

                Position = 0;
                _samplesSinceAccepted = 0;
                _hasEverAccepted = true;
                return true;
            }

            if (Position >= PeriodEstimate)
            {
                Position -= PeriodEstimate;
                return true;
            }

            return false;
        }

        private bool IsWithinCaptureRange()
        {
            if (!_hasEverAccepted)
            {
                return true;
            }

            return Math.Abs(_samplesSinceAccepted - PeriodEstimate) <= PeriodEstimate * captureRangeFraction;
        }
    }
}
