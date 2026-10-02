using System;

namespace Aemula.Emulation.Output.Composite;

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
// knob's center frequency), which incoming HSYNC/VSYNC pulses pull into
// phase - within a limited "capture range" - rather than being a clock
// that sync simply defines. Pulses that land outside that capture range
// are noise, not sync, and get ignored; and if no valid pulses show up for
// a while, the oscillator just keeps going on its own (a "flywheel"),
// producing a torn or rolling picture rather than freezing or crashing.
// This is what makes the whole decoder behave sensibly - not perfectly,
// but the same way a real set would - on a badly out-of-spec signal.
//
// The two are not pulled into phase the same way, because on a real
// receiver they genuinely aren't the same kind of circuit. Composite sync
// leaves the sync separator and splits two ways:
//
//   Horizontal goes to an AFC ("flywheel sync"): a phase detector compares
//   the incoming pulse against a waveform derived from the horizontal
//   output stage's own flyback, and the error runs through an RC
//   "anti-hunt" filter that nudges the oscillator's *frequency*. The
//   oscillator's phase is never yanked to the pulse. Directly-triggered
//   horizontal sync was abandoned around 1950 for exactly this reason: a
//   noise pulse landing mid-line would drag the phase with it and shred
//   the picture. It's also why a misadjusted horizontal hold skews and
//   tears and then pulls itself back in.
//
//   Vertical goes through an integrator network to a blocking oscillator
//   or multivibrator that the resulting broad pulse triggers outright. The
//   vertical hold control sets it to free-run slightly slow so that sync
//   triggers it early every field. That really is a hard reset - which is
//   why a misadjusted vertical hold *rolls* rather than tearing: the
//   oscillator is free-running, never being triggered at all.
//
// Both behaviors come out of one PullInOscillator, parameterized by how
// much of a phase error one accepted pulse is allowed to remove - see
// HorizontalPhaseCorrectionRate/VerticalPhaseCorrectionRate below.
public sealed class RasterOscillators
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
    // entirely (see the out-of-range test in RasterOscillatorsTests).
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

    // How much of a measured phase error one accepted pulse is allowed to
    // remove - the difference in *kind* between the two oscillators, per the
    // class remarks above, expressed as one number each.
    //
    // Horizontal is the AFC's anti-hunt filter: 5% per line, so a phase error
    // decays with a time constant of ~20 lines (~1.3ms, comfortably inside a
    // field, which is the pull-in speed real AFC designs aim for). The point
    // is the *stiffness*, not the exact figure - a single stray pulse moves
    // the phase by a twentieth of its own error rather than capturing it
    // outright, and a genuine one-off timing wobble in the source is absorbed
    // as a sub-sample-per-line drift rather than a step. Setting this to 1
    // would turn the horizontal oscillator back into the directly-triggered
    // design that AFC replaced.
    private const float HorizontalPhaseCorrectionRate = 0.05f;

    // Vertical is a trigger, so its correction is the whole error: the
    // integrated sync pulse fires the oscillator and that *is* the new phase.
    // 1 here reproduces a plain "Position = 0" snap exactly (see
    // PullInOscillator.Accept) - it isn't an approximation of one.
    private const float VerticalPhaseCorrectionRate = 1f;

    // A real vertical sync region is several HSYNC-width-or-broader pulses
    // in a row (equalizing + broad serration pulses), not one - see the
    // class remarks on SyncSeparator. Any VSYNC-classified pulse
    // arriving within this many *current horizontal line lengths* of the
    // last one considered is treated as part of the same vertical-blanking
    // region, not a fresh field boundary - empirically, real vertical sync
    // regions in this codebase's two test signals span up to ~3.5 line
    // widths, so this leaves comfortable margin while staying utterly
    // negligible next to the ~262-line gap between genuine fields.
    private const float VerticalDebounceLineMultiplier = 4.0f;

    private readonly PullInOscillator _horizontal;
    private readonly PullInOscillator _vertical;

    // How long it's been since the last VSYNC-classified pulse was even
    // considered (accepted or not) - the debounce gate described above.
    // Seeded huge so the very first VSYNC pulse in a stream is always
    // considered.
    private float _samplesSinceLastVSyncCandidate = 1e12f;

    // The samples-per-line figure CurrentRow divides the vertical ramp by,
    // held fixed for a whole field and only re-read at each vertical
    // boundary. On a real set the vertical deflection's ramp maps to a
    // physical height through the yoke's sensitivity - a fixed circuit
    // constant, not something that moves while the field is being drawn -
    // and this is the modelling equivalent.
    //
    // Reading _horizontal.PeriodEstimate live here instead (which is what
    // this did) makes the row index a quotient of two independently moving
    // quantities, so a horizontal period estimate that grows mid-field
    // silently rescales the whole row axis underneath the ramp. The row
    // index can then step *backwards* even though the ramp only ever
    // advances - and it did: one long scanline in a ZX80 field nudged the
    // estimate up, the row index went 225 -> 224, and the next line's
    // samples (its sync pulse included) were written back over a row
    // already drawn, as a dark bar inside the visible picture. A real
    // vertical ramp cannot revisit a height it has already swept past.
    private float _rowScaleSamplesPerLine;

    public RasterOscillators(TelevisionTiming? timing = null)
    {
        timing ??= TelevisionTiming.Ntsc;

        _horizontal = new(
            timing.SamplesPerLine,
            HorizontalCaptureRangeFraction,
            HorizontalSmoothingRate,
            HorizontalPhaseCorrectionRate);

        _vertical = new(
            timing.SamplesPerField,
            VerticalCaptureRangeFraction,
            VerticalSmoothingRate,
            VerticalPhaseCorrectionRate);

        _rowScaleSamplesPerLine = timing.SamplesPerLine;
    }

    /// <summary>
    /// The raster column (sample position within the current line) of the
    /// sample just processed.
    /// </summary>
    public int CurrentColumn => (int)_horizontal.Position;

    /// <summary>
    /// The raster row (line position within the current field) of the
    /// sample just processed - how far down its own ramp the vertical
    /// oscillator has swept, measured in line-heights. Monotonic within a
    /// field by construction: the ramp only advances, and the scale it is
    /// divided by is held fixed across the field (see
    /// <see cref="_rowScaleSamplesPerLine"/>).
    /// </summary>
    public int CurrentRow => (int)(_vertical.Position / _rowScaleSamplesPerLine);

    /// <summary>
    /// The current running estimate of samples-per-line, measured from real
    /// HSYNC spacing rather than configured.
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
    /// sample's <see cref="SyncSeparator.HSyncDetected"/> and
    /// <see cref="SyncSeparator.VSyncDetected"/>.
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
        if (_vertical.Tick(offerVSync))
        {
            // Vertical retrace: the one moment the row scale can change
            // without rewriting the field currently being drawn - see
            // _rowScaleSamplesPerLine.
            _rowScaleSamplesPerLine = _horizontal.PeriodEstimate;
        }
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
    //      zero (and reporting a boundary crossing) when a full period's
    //      worth of samples has gone by on its own. An accepted pulse also
    //      reports a boundary crossing, and pulls Position toward the
    //      pulse by phaseCorrectionRate's share of the error - a whole
    //      trigger at 1, an AFC's filtered nudge below that.
    private sealed class PullInOscillator(
        float nominalPeriod,
        float captureRangeFraction,
        float smoothingRate,
        float phaseCorrectionRate)
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
        // RasterOscillatorsTests. Starts at 0, in step with Position -
        // it's also used as *this* accept's measured period (see Tick
        // below), so it has to genuinely reflect "samples since Tick
        // started counting" even for the very first accept, not just be a
        // large sentinel; _hasEverAccepted (not this) is what guarantees
        // that very first pulse is accepted regardless of its value.
        private float _samplesSinceAccepted;
        private bool _hasEverAccepted;

        // Re-acquisition tracking - deliberately independent of
        // _samplesSinceAccepted above. A real captured Atari2600 signal
        // exposed a genuine deadlock this fixes: one anomalous or
        // misclassified pulse (confirmed against a real 2600 ROM - an
        // early, not-yet-settled TIA horizontal-counter state produced one
        // pulse shaped like neither a clean HSYNC nor a clean VSYNC) can
        // leave _samplesSinceAccepted stranded outside captureRangeFraction
        // of PeriodEstimate forever, since that counter only ever resets on
        // an accept, and PeriodEstimate only ever moves via one - every
        // later, perfectly genuine pulse then also gets rejected, with no
        // way back. This tracks time since the last *offered* pulse
        // regardless of whether it was accepted, so several consecutive
        // offers landing at the same new interval - evidence of a real
        // recurring signal, not noise - can re-lock even from that dead
        // stop. Mirrors how a real hardware AFC/vertical-hold circuit
        // recovers after losing lock rather than staying derailed by one
        // glitch forever - see the class doc-comment's real-hardware
        // capture-range citations.
        private float _samplesSinceLastOffer = float.MaxValue;
        private float _lastOfferedInterval;
        private int _consistentOfferStreak;

        // How many consecutive offered pulses at a self-consistent interval
        // it takes to re-lock - enough that a single stray pulse landing
        // near a genuine one by coincidence can't trigger it, small enough
        // to recover within a handful of lines/fields of a real signal
        // reappearing.
        private const int ReacquisitionConfirmCount = 3;

        // How close two consecutive offered-pulse intervals need to be to
        // count as "the same recurring signal" for re-acquisition - wider
        // than captureRangeFraction on purpose: this is asking "is this
        // periodic at all", not "does it already match our stale estimate".
        private const float ReacquisitionIntervalToleranceFraction = 0.05f;

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
            _samplesSinceLastOffer += 1f;

            if (pulseOffered)
            {
                if (IsWithinCaptureRange())
                {
                    // A period is a measurement *between* two points - the
                    // very first pulse this oscillator ever sees only
                    // establishes where "phase zero" is, the same way a
                    // real set's oscillator has to grab an arbitrary first
                    // reference point before it has any interval to measure
                    // yet. Nudging PeriodEstimate toward that first
                    // (essentially arbitrary, depends only on where in the
                    // stream decoding happened to start) Position would
                    // just be adding noise, not signal - wait for the
                    // *second* accept, which is the first one with a
                    // genuine measured interval behind it.
                    if (_hasEverAccepted)
                    {
                        var measuredPeriod = _samplesSinceAccepted;
                        var target = PeriodEstimate + (measuredPeriod - PeriodEstimate) * smoothingRate;

                        // A real oscillator can be nudged, not retuned -
                        // bounding the estimate to a fixed band around
                        // nominal is what guarantees a wildly-wrong pulse
                        // train can never be mistaken for genuine sync, no
                        // matter how many times it's (partially) accepted -
                        // see MaxPeriodDriftFraction above.
                        PeriodEstimate = Math.Clamp(
                            target,
                            _nominalPeriod * (1 - MaxPeriodDriftFraction),
                            _nominalPeriod * (1 + MaxPeriodDriftFraction));

                        Accept(phaseCorrectionRate);
                    }
                    else
                    {
                        // The first pulse doesn't correct a phase, it
                        // establishes one - there's nothing to filter
                        // toward yet, the same reason it doesn't touch
                        // PeriodEstimate either. A real set has to grab an
                        // arbitrary first reference the same way, before
                        // its AFC has any error to work on.
                        Accept(1f);
                    }

                    return true;
                }

                // Outside the fine-lock capture range - see whether this
                // offer is at least consistent with the previous one
                // (rather than immediately giving up on it, which is what
                // deadlocked this oscillator against the real signal
                // described above).
                if (_consistentOfferStreak > 0 &&
                    Math.Abs(_samplesSinceLastOffer - _lastOfferedInterval) <= _lastOfferedInterval * ReacquisitionIntervalToleranceFraction)
                {
                    _consistentOfferStreak++;
                }
                else
                {
                    _consistentOfferStreak = 1;
                }

                if (_consistentOfferStreak >= ReacquisitionConfirmCount)
                {
                    // Re-lock directly to the newly measured interval
                    // rather than smoothing toward it, the same way the
                    // very first accept above also skips smoothing - this
                    // is recovery from a lost lock, not a fine adjustment
                    // to an already-good estimate.
                    PeriodEstimate = Math.Clamp(
                        _samplesSinceLastOffer,
                        _nominalPeriod * (1 - MaxPeriodDriftFraction),
                        _nominalPeriod * (1 + MaxPeriodDriftFraction));

                    // Re-lock takes the phase outright, not filtered -
                    // lock was lost, so there is no useful phase to
                    // preserve, and the same reasoning that skips
                    // smoothing on PeriodEstimate just above applies here.
                    Accept(1f);
                    return true;
                }

                _lastOfferedInterval = _samplesSinceLastOffer;
                _samplesSinceLastOffer = 0;
            }

            if (Position >= PeriodEstimate)
            {
                Position -= PeriodEstimate;
                return true;
            }

            return false;
        }

        // Pulls Position toward this pulse by rate's share of the phase
        // error, rather than assigning it. At rate 1 that is arithmetically
        // identical to the "Position = 0" this used to do; below 1 it is an
        // AFC's filtered correction - see HorizontalPhaseCorrectionRate.
        private void Accept(float rate)
        {
            // Signed, and wrapped into +/- half a period: a pulse arriving
            // slightly *early* catches Position just short of a full period,
            // which is a small negative error, not an enormous positive one.
            // Without the wrap that reads as nearly a whole period of error
            // and gets "corrected" the long way round.
            var phaseError = Position;
            if (phaseError > PeriodEstimate * 0.5f)
            {
                phaseError -= PeriodEstimate;
            }

            Position -= phaseError * rate;

            // A partial correction can leave Position a little outside
            // [0, PeriodEstimate); a full one lands exactly on 0 or exactly
            // on PeriodEstimate, which is the same instant one period later.
            if (Position < 0f)
            {
                Position += PeriodEstimate;
            }
            else if (Position >= PeriodEstimate)
            {
                Position -= PeriodEstimate;
            }

            _samplesSinceAccepted = 0;
            _hasEverAccepted = true;
            _consistentOfferStreak = 0;
            _samplesSinceLastOffer = 0;
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
