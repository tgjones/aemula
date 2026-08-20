using System;

namespace Aemula.Emulation.Output.Ntsc;

// Phase 3 of docs/television-plan.md.
//
// Every line's back porch carries a short burst of the color subcarrier -
// not picture information, just a reference: "here is what 0 degrees of
// color phase looks like, right now." A receiver needs this because the
// subcarrier isn't sent as a separate wire alongside the picture - it's
// mixed directly into the composite signal (that's what makes NTSC "com-
// posite" in the first place), so the only way to know its *current phase*
// is to measure a piece of pure subcarrier the transmitter deliberately
// includes for exactly this purpose. Get the phase wrong and every color
// in the picture comes out hue-shifted by the same wrong amount.
//
// This class is that measurement, done the way real burst-locked
// oscillators are actually built: not by re-deriving the phase from
// scratch every line, but by keeping one persistent local oscillator and
// only ever *nudging* it a little via the burst it sees, line after line -
// a genuine phase-locked loop (a phase detector feeding a loop filter into
// the oscillator's own phase state), not "read the sign of a few samples
// and reset a variable".
//
// The 4x-the-subcarrier sample rate this whole decoder assumes (see
// docs/television-plan.md's "Input signal contract") is what keeps the
// "local oscillator" almost trivial to generate: every sample is either 0,
// 90, 180, or 270 degrees of subcarrier phase, in a fixed repeating
// sequence - so there's no unknown *frequency* to track here, only an
// unknown, slowly-drifting *phase offset* against that fixed 4-step
// sequence.
public sealed class NtscColorBurstPll
{
    // How much of the measured phase error to actually apply each line -
    // deliberately small (a proportional-only loop, not a full PI
    // controller: there's no frequency error to correct given the fixed
    // 4x-subcarrier sample rate, just a phase nudge) so the estimate
    // settles smoothly across many lines rather than chasing noise on any
    // one of them. A free parameter with no single correct value from
    // first principles - see docs/television-plan.md's Open risks.
    private const double LoopGain = 0.1;

    // A completed burst window only counts as "burst was actually there"
    // if its measured amplitude clears this fraction of the black/white
    // swing - otherwise it's active-video content or noise that happened
    // to fall in the window, not a real reference burst. Chosen
    // empirically against smpte.ntsc's real burst amplitude (see
    // NtscColorBurstPllTests) - a free parameter, not derived from spec.
    private const double DetectionThresholdFraction = 0.05;

    // Free-running count of samples this PLL has ever processed - (mod 4)
    // is which of the 4 fixed reference phases (0/90/180/270 degrees) the
    // *next* sample lands on, before the persistent phase-offset
    // correction below is applied. Never reset per-line or per-field,
    // matching real hardware where the local oscillator is one continuous
    // free-running thing, not something rebuilt every line.
    private ulong _sampleCounter;

    // The one piece of state this whole class exists to maintain: how far
    // (in radians) the local oscillator's phase-0 reference is offset from
    // the raw "_sampleCounter mod 4" sequence above. Persists across
    // lines, and even across lines with no detectable burst at all (see
    // Process below) - a real burst-locked oscillator keeps "ringing" at
    // its last-known phase between bursts rather than resetting.
    private double _phaseOffsetRadians;

    // This line's in-progress burst-window correlation, accumulated
    // sample-by-sample while inside the window and finalized (feeding the
    // loop filter) the moment the window closes - see Process.
    private double _inPhaseAccumulator;
    private double _quadratureAccumulator;
    private int _windowSampleCount;

    /// <summary>
    /// The local oscillator's current phase-offset correction, in radians -
    /// mostly useful for tests/diagnostics, since <see cref="Process"/> is
    /// what actually applies it to demodulation.
    /// </summary>
    public double PhaseOffsetRadians => _phaseOffsetRadians;

    /// <summary>
    /// Whether a real burst (not just active-video content that happened
    /// to fall in the expected window) was found on the most recently
    /// completed line.
    /// </summary>
    public bool BurstDetected { get; private set; }

    /// <summary>
    /// Whether the sample most recently passed to <see cref="Process"/> fell
    /// within the color-burst window - live, per sample, unlike
    /// <see cref="BurstDetected"/> (which only finalizes once a whole line's
    /// window has closed). This is the literal window this class's own
    /// phase detector correlates that sample against, not a separately
    /// re-derived one - see docs/television-plan.md's Phase 7, which needed
    /// a per-sample "was this really burst" answer sourced from the same
    /// decision the pipeline already made, not a second reconstruction of
    /// it.
    /// </summary>
    public bool IsInBurstWindow { get; private set; }

    /// <summary>
    /// The local oscillator's resolved phase for the sample most recently
    /// passed to <see cref="Process"/> - the literal "where is 0/90/180/270
    /// degrees of the recovered color subcarrier, right now" reference this
    /// PLL locks to burst, <em>before</em> <see cref="NtscYiqDecoder"/>'s own
    /// further rotation onto the I axis (see that class's
    /// BurstToIAxisRotationRadians remarks - that rotation is specific to
    /// demodulating I/Q, not part of what "the color carrier" itself means).
    /// Mainly useful for diagnostics (e.g. TelevisionWindow's per-sample
    /// hover tooltip drawing this as a reference sine over the raw signal).
    /// </summary>
    public double CurrentPhaseRadians { get; private set; }

    /// <summary>
    /// Feeds one composite-video sample into the PLL. <paramref name="currentColumn"/>
    /// should be <see cref="NtscRasterOscillators.CurrentColumn"/> for this
    /// same sample, and <paramref name="blackLevel"/> should be
    /// <see cref="NtscSyncSeparator.BlackLevel"/> - burst oscillates around
    /// the black/blanking level, not around byte value zero, so it has to
    /// be re-centered before correlating against the local oscillator.
    /// <paramref name="whiteLevel"/> is only used to scale the detection
    /// threshold to this signal's own black-to-white swing.
    /// </summary>
    public void Process(byte sample, double currentColumn, double blackLevel, double whiteLevel)
    {
        var phase = Math.PI / 2.0 * (_sampleCounter % 4) + _phaseOffsetRadians;
        _sampleCounter++;
        CurrentPhaseRadians = phase;

        IsInBurstWindow = currentColumn >= NtscTiming.BurstWindowStartSamples
            && currentColumn < NtscTiming.BurstWindowStartSamples + NtscTiming.BurstWindowLengthSamples;

        if (IsInBurstWindow)
        {
            var acSample = sample - blackLevel;

            // The classic quadrature phase-detector correlation: multiply
            // the incoming (DC-removed) sample by the local oscillator's
            // own in-phase and quadrature references and accumulate. Over
            // a whole number of cycles, a sinusoid perfectly aligned with
            // the in-phase reference correlates entirely onto
            // _inPhaseAccumulator and leaves _quadratureAccumulator at
            // zero; any nonzero quadrature accumulation *is* the phase
            // error this loop corrects.
            _inPhaseAccumulator += acSample * Math.Cos(phase);
            _quadratureAccumulator += acSample * Math.Sin(phase);
            _windowSampleCount++;
        }
        else if (_windowSampleCount > 0)
        {
            FinishBurstWindow(whiteLevel - blackLevel);
        }
    }

    private void FinishBurstWindow(double blackToWhiteSwing)
    {
        // Standard quadrature-demodulation amplitude recovery: for a pure
        // sinusoid correlated against sine/cosine references over N
        // samples, the true amplitude is 2x the correlation vector's
        // magnitude divided by N (the factor of 2 falls out of the same
        // trig identity that makes I/Q demodulation work at all - see
        // NtscYiqDecoder in a later phase for the same math applied to
        // chroma).
        var amplitude = 2.0 * Math.Sqrt(
            _inPhaseAccumulator * _inPhaseAccumulator + _quadratureAccumulator * _quadratureAccumulator)
            / _windowSampleCount;

        BurstDetected = amplitude >= blackToWhiteSwing * DetectionThresholdFraction;

        if (BurstDetected)
        {
            // Normalizing by the measured amplitude turns the raw
            // quadrature accumulation into sin(phase error) for small
            // errors, regardless of how strong this particular line's
            // burst happened to be - LoopGain is then a tuning constant
            // for how fast the loop settles, not one that also has to
            // account for arbitrary signal amplitude.
            var normalizedError = _quadratureAccumulator / (_windowSampleCount * amplitude / 2.0);
            _phaseOffsetRadians -= normalizedError * LoopGain;
        }

        // Whether or not burst was found, reset for the next line - but
        // note _phaseOffsetRadians itself is untouched when burst wasn't
        // found: the flywheel behavior the plan doc calls for. A missed
        // burst (weak signal, noise, a blanking line with no picture at
        // all) doesn't cause a visible hue glitch, it just means this one
        // line's phase estimate didn't get refined.
        _inPhaseAccumulator = 0;
        _quadratureAccumulator = 0;
        _windowSampleCount = 0;
    }
}
