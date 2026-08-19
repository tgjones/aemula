using System;

namespace Aemula.Emulation.Output.Ntsc;

// Phase 4 of docs/television-plan.md.
//
// Everything up to this point (NtscSyncSeparator, NtscRasterOscillators,
// NtscColorBurstPll) exists to answer "where in the raster is this sample,
// and what phase is the color subcarrier at" - genuinely important
// questions, but none of them turn a sample into a *pixel*. This class is
// the last step: given one composite-video sample and the phase/timing
// context the earlier stages already worked out, produce one RGB pixel.
//
// Composite video mixes two very different kinds of information into one
// waveform: "luma" (Y - brightness, the same signal a black-and-white TV
// already knows how to show) and "chroma" (color, riding on top of luma as
// a high-frequency wiggle at exactly the subcarrier frequency). Separating
// them, and then decoding chroma's own phase/amplitude into a hue/
// saturation pair (I and Q), is the classic problem this class solves in
// three steps below.
public sealed class NtscYiqDecoder
{
    // The fixed rotation between the color-burst PLL's own phase-zero
    // reference (which NtscColorBurstPll locks to wherever the burst
    // signal's positive peak happens to land) and the NTSC-standard I axis
    // the YIQ->RGB matrix below assumes - derived from the standard NTSC
    // Y'UV/Y'IQ axis geometry, not fitted to smpte.ntsc's bar colors:
    //
    //   - I is *defined* as the (B'-Y')/(R'-Y') plane's "V" axis
    //     (=(R'-Y')'s own direction), rotated by exactly 33 degrees - this
    //     is the actual historical definition (the 0.956/0.621/etc. YIQ->RGB
    //     coefficients below are *derived from* this 33-degree rotation
    //     together with the 0.492/0.877 U/V scale factors, not the other
    //     way around) - see e.g. Poynton, "Digital Video and HDTV", the
    //     classic Y'UV/Y'IQ vector diagram. NtscYiqDecoderTests'
    //     MatchesUvToIqDefinition test reconstructs the standard 0.596/
    //     -0.274/-0.322/0.211/-0.523/0.312 matrix coefficients from this
    //     same 33-degree figure, as a check that this really is the
    //     defining relationship and not just a coincidentally-close number.
    //   - V sits 90 degrees from U by definition of the (U, V) plane, so I
    //     sits at 90+33 = 123 degrees from the U axis.
    //   - the color burst is transmitted in antiphase to U (burst = -U) -
    //     the standard "burst references the (B'-Y') axis, 180 degrees
    //     out of phase" fact (also why a vectorscope's burst target sits
    //     opposite the U axis) - so the angle from burst's own phase to the
    //     I axis is 123 - 180 = -57 degrees.
    //
    // That -57-degree figure is itself a commonly-cited standalone NTSC
    // fact ("burst leads I by 57 degrees" / "I is 57 degrees behind
    // burst"), corroborating the geometric derivation above independently.
    //
    // What spec derivation *can't* pin down is which of two directions this
    // particular implementation needs the correction applied in: burst's
    // phase is recovered by NtscColorBurstPll's phase detector, which - like
    // any squaring/Costas-style detector (see that class's remarks) -
    // cannot distinguish a lock from a lock 180 degrees away, since burst =
    // +A*cos(phase) and burst = -A*cos(phase) both drive its quadrature
    // error to zero equally well. Which of the two this implementation's
    // loop actually settles into for a given real signal isn't something
    // the broadcast spec specifies (it depends on this decoder's own
    // cos/sin-to-in-phase/quadrature assignment and the real signal's
    // recorded polarity) - exactly the kind of thing a real TV's "tint"
    // knob exists to compensate for. Resolved here, once, against the one
    // real reference this project has (confirmed empirically: 180 degrees
    // added to the -57-degree spec figure, i.e. +123 degrees, is what
    // actually locks this implementation to the real burst - see
    // TelevisionTests' SMPTE bar assertions).
    private const double IAxisFromVAxisDegrees = 33.0;
    private const double VAxisFromUAxisDegrees = 90.0;
    private const double BurstFromUAxisDegrees = 180.0;
    private const double SpecBurstToIAxisDegrees =
        (VAxisFromUAxisDegrees + IAxisFromVAxisDegrees) - BurstFromUAxisDegrees; // -57

    private const double PllLockBranchDegrees = 180.0;

    internal const double BurstToIAxisRotationRadians =
        (SpecBurstToIAxisDegrees + PllLockBranchDegrees) * Math.PI / 180.0; // +123 degrees

    // The comb filter (see Process below) and the I/Q box-average both work
    // over a rolling one-subcarrier-cycle (4-sample) window, so both keep a
    // small ring buffer of recent values rather than reaching back into the
    // full sample stream.
    private readonly byte[] _sampleHistory = new byte[5];
    private readonly double[] _iProductHistory = new double[4];
    private readonly double[] _qProductHistory = new double[4];

    private ulong _sampleCounter;

    /// <summary>
    /// The most recently decoded luma (brightness), rescaled so black = 0
    /// and white = 255 - <see cref="Television.Decode"/>'s own byte scale,
    /// not the raw sample scale (see Process).
    /// </summary>
    public double Luma { get; private set; }

    /// <summary>
    /// The most recently decoded in-phase chroma component, on the same
    /// black-to-white scale as <see cref="Luma"/> (0 = no color).
    /// </summary>
    public double I { get; private set; }

    /// <summary>
    /// The most recently decoded quadrature chroma component, on the same
    /// black-to-white scale as <see cref="Luma"/> (0 = no color).
    /// </summary>
    public double Q { get; private set; }

    /// <summary>
    /// The most recently decoded pixel, as an opaque RGB byte triple.
    /// </summary>
    public RgbaByte Rgb { get; private set; }

    /// <summary>
    /// Decodes one composite-video sample into a pixel. <paramref name="phaseOffsetRadians"/>
    /// should be <see cref="NtscColorBurstPll.PhaseOffsetRadians"/>, and
    /// <paramref name="blackLevel"/>/<paramref name="whiteLevel"/> should be
    /// <see cref="NtscSyncSeparator.BlackLevel"/>/<see cref="NtscSyncSeparator.WhiteLevel"/>
    /// for this same sample - this class doesn't know or care whether the
    /// sample it's given actually falls in active video; callers only need
    /// to consult <see cref="NtscYiqDecoder"/>'s output where
    /// <c>Television.IsActiveVideo</c> is true (sync/blanking samples decode
    /// to meaningless colors, harmlessly, since nothing displays them).
    /// </summary>
    public void Process(byte sample, double phaseOffsetRadians, double blackLevel, double whiteLevel)
    {
        // Step 1: luma via a comb filter. Every sample is exactly 90 degrees
        // of subcarrier phase from its neighbors (the 4x-fsc assumption -
        // see docs/television-plan.md's "Input signal contract"), so a
        // sample 2 positions back is exactly 180 degrees - i.e. exactly
        // inverted - chroma, and a sample 4 positions back is a full cycle
        // (360 degrees) - i.e. same-phase - chroma. Weighting those three
        // taps 1:2:1 (X[n] + 2*X[n-2] + X[n-4]) makes the two 180-degree-
        // apart pairs (n & n-2, n-2 & n-4) cancel chroma's contribution
        // completely for a pure single-frequency chroma signal - the
        // standard 3-tap NTSC notch/comb filter - while luma (which isn't
        // oscillating at the subcarrier frequency) survives averaging
        // mostly untouched, since it barely changes sample to sample.
        Array.Copy(_sampleHistory, 0, _sampleHistory, 1, _sampleHistory.Length - 1);
        _sampleHistory[0] = sample;

        var rawLuma = (_sampleHistory[0] + 2.0 * _sampleHistory[2] + _sampleHistory[4]) / 4.0;

        // Step 2: chroma is simply whatever's left after luma is removed.
        var rawChroma = sample - rawLuma;

        // Rescale from this signal's own self-calibrated sync/black/white
        // levels (see NtscSyncSeparator) onto the fixed 0-255 black-to-white
        // scale the YIQ->RGB matrix below assumes - the same rescale factor
        // applies to chroma, since chroma's amplitude lives in the same
        // volts/byte units as luma does.
        var scale = 255.0 / (whiteLevel - blackLevel);
        Luma = Math.Clamp((rawLuma - blackLevel) * scale, 0, 255);
        var chroma = rawChroma * scale;

        // Step 3: I/Q quadrature demodulation. Multiplying chroma by the
        // burst-locked local oscillator's in-phase/quadrature references and
        // averaging over one full subcarrier cycle isolates I and Q from
        // the 2x-subcarrier-frequency term the multiplication also
        // produces (that term averages to exactly zero over any 4
        // consecutive samples, the same trick the comb filter above uses) -
        // and, like NtscColorBurstPll.FinishBurstWindow's amplitude
        // recovery (which this is "the same math applied to chroma", per
        // that class's own remarks), the raw average needs multiplying by 2
        // to recover the true I/Q amplitude, not just a scaled-down
        // version of it.
        var slot = (int)(_sampleCounter % 4);
        var phase = Math.PI / 2.0 * slot + phaseOffsetRadians + BurstToIAxisRotationRadians;
        _sampleCounter++;

        _iProductHistory[slot] = chroma * Math.Cos(phase);
        _qProductHistory[slot] = chroma * Math.Sin(phase);

        var iSum = 0.0;
        var qSum = 0.0;
        for (var i = 0; i < 4; i++)
        {
            iSum += _iProductHistory[i];
            qSum += _qProductHistory[i];
        }

        I = 2.0 * iSum / 4.0;
        Q = 2.0 * qSum / 4.0;

        // Step 4: YIQ -> RGB. Real hardware does this with three resistor-
        // ratio-weighted analog summing amplifiers (the same "weighted sum"
        // pattern AppleIISystem.CompositeVideo.cs's Q3 encoder stage uses,
        // just run in reverse) - a fixed linear transform, not a lookup
        // table. Coefficients are the standard NTSC/FCC-derived matrix
        // (independently confirmed via MATLAB's ntsc2rgb and the classic
        // FCC-derived coefficients commonly reproduced in video-engineering
        // references - see the plan doc's YIQ section); different sources'
        // coefficients drift very slightly, but this set is the most
        // commonly cited one and well within this project's accuracy bar.
        var r = Luma + 0.956 * I + 0.621 * Q;
        var g = Luma - 0.272 * I - 0.647 * Q;
        var b = Luma - 1.106 * I + 1.703 * Q;

        Rgb = new RgbaByte(ClampToByte(r), ClampToByte(g), ClampToByte(b), 255);
    }

    private static byte ClampToByte(double value) => (byte)Math.Clamp(value, 0, 255);
}
