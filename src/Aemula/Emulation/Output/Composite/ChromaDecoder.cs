using System;
using System.Runtime.Intrinsics;

namespace Aemula.Emulation.Output.Composite;

// Everything up to this point (SyncSeparator, RasterOscillators,
// ColorBurstPll) exists to answer "where in the raster is this sample,
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
public sealed class ChromaDecoder
{
    // The NTSC derivation of this rotation lives with the standard's data (see
    // TelevisionStandard); this alias is what the tests derive their phases from.
    internal static readonly double BurstToIAxisRotationRadians =
        TelevisionStandard.Ntsc.BurstToFirstAxisRadians;

    // Step 4's R/G/B coefficients (see Process), laid out as one lane per
    // output channel (the unused 4th lane keeps these Vector128<float>-width
    // for the FusedMultiplyAdd below - see Process for why 4 lanes are always
    // available regardless of host SIMD width). Rows A and B are the standard's
    // first and second demodulated component (I/Q for NTSC, U/V for PAL).
    private readonly Vector128<float> _rgbCoeffA;
    private readonly Vector128<float> _rgbCoeffB;
    private readonly float _baseAngleOffset;
    private readonly float _secondAxisPolarity;

    // The decode gain must not depend on how bright the current scene
    // happens to be, so reference white is reconstructed from the sync tip and
    // blanking levels rather than a running picture peak - see
    // TelevisionStandard.ReferenceWhiteGainFromSyncSwing. Scaling off a
    // running picture-peak max instead (as this once did) inflates gain on any
    // persistently-dim signal.
    private readonly TelevisionStandard _standard;

    public ChromaDecoder(TelevisionStandard? standard = null)
    {
        _standard = standard ?? TelevisionStandard.Ntsc;

        var a = _standard.RgbFromFirstAxis;
        var b = _standard.RgbFromSecondAxis;
        _rgbCoeffA = Vector128.Create(a.R, a.G, a.B, 0f);
        _rgbCoeffB = Vector128.Create(b.R, b.G, b.B, 0f);
        _baseAngleOffset = (float)_standard.BurstToFirstAxisRadians;
        _secondAxisPolarity = _standard.SecondAxisPolarity;
    }

    // The comb filter (see Process below) and the I/Q box-average both work
    // over a rolling one-subcarrier-cycle (4-sample) window, so both keep a
    // small ring buffer of recent values rather than reaching back into the
    // full sample stream.
    //
    // float, not double, throughout this class: every value here ultimately
    // gets clamped down to a single output byte (0-255), so float's 24-bit
    // mantissa is far more precision than the result can ever show, and
    // nothing recursively accumulates error sample-to-sample (each sample's
    // phase is recomputed fresh from phaseOffsetRadians, not integrated) -
    // see the ChromaDecoder float-vs-double discussion in chat history for
    // the full reasoning.
    private readonly byte[] _sampleHistory = new byte[5];
    private readonly float[] _iProductHistory = new float[4];
    private readonly float[] _qProductHistory = new float[4];

    private ulong _sampleCounter;

    // sin/cos of the demodulation base angle, memoised across samples. The
    // base angle is phaseOffsetRadians (the color-burst PLL's slowly-drifting
    // lock) plus a fixed rotation constant, and the PLL only nudges its offset
    // once per line (ColorBurstPll.FinishBurstWindow) - so this argument
    // holds steady for a whole line's ~900 samples, and recomputing SinCos
    // every sample was pure waste. Recompute only when the angle actually
    // moves; NaN-safe because the initial _lastBaseAngle != any real angle.
    private float _lastBaseAngle = float.NaN;
    private float _lastBaseSin;
    private float _lastBaseCos;

    /// <summary>
    /// The most recently decoded luma (brightness), rescaled so black = 0
    /// and white = 255 - <see cref="Television.Decode"/>'s own byte scale,
    /// not the raw sample scale (see Process).
    /// </summary>
    public float Luma { get; private set; }

    /// <summary>
    /// The most recently separated chroma (the signal minus its comb-filtered
    /// luma), on the same black-to-white scale as <see cref="Luma"/> and
    /// signed around zero. Zero while the color killer is engaged. This is
    /// the waveform the I/Q demodulator is fed.
    /// </summary>
    public float Chroma { get; private set; }

    // I and Q are NTSC's names for the two demodulated components; for PAL they
    // are U and V (see TelevisionStandard).
    /// <summary>
    /// The most recently decoded in-phase chroma component, on the same
    /// black-to-white scale as <see cref="Luma"/> (0 = no color).
    /// </summary>
    public float I { get; private set; }

    /// <summary>
    /// The most recently decoded quadrature chroma component, on the same
    /// black-to-white scale as <see cref="Luma"/> (0 = no color).
    /// </summary>
    public float Q { get; private set; }

    /// <summary>
    /// The most recently decoded pixel, as an opaque RGB byte triple.
    /// </summary>
    public RgbaByte Rgb { get; private set; }

    /// <summary>
    /// Decodes one composite-video sample into a pixel. <paramref name="phaseOffsetRadians"/>
    /// should be <see cref="ColorBurstPll.PhaseOffsetRadians"/>, and
    /// <paramref name="blackLevel"/>/<paramref name="syncLevel"/> should be
    /// <see cref="SyncSeparator.BlackLevel"/>/<see cref="SyncSeparator.SyncLevel"/>
    /// for this same sample - reference white is reconstructed from those
    /// two points (see <see cref="TelevisionStandard.WhiteReference"/>), not taken from a
    /// running picture peak. This class doesn't know or care whether the
    /// sample it's given actually falls in active video; callers only need
    /// to consult <see cref="ChromaDecoder"/>'s output where
    /// <c>Television.IsActiveVideo</c> is true (sync/blanking samples decode
    /// to meaningless colors, harmlessly, since nothing displays them).
    /// <paramref name="colorBurstDetected"/> should be
    /// <see cref="ColorBurstPll.BurstDetected"/> for the line this sample
    /// belongs to: when it is false this acts as a real receiver's color
    /// killer and mutes the chroma path entirely (see below), so a
    /// burst-less source (a monochrome signal, or an Apple II with its
    /// color-killer circuit suppressing burst in text mode) decodes as
    /// grayscale instead of pulling spurious hue out of sharp edges.
    /// <paramref name="secondAxisSign"/> should be
    /// <see cref="ColorBurstPll.SecondAxisSign"/> (always +1 for NTSC).
    /// </summary>
    public void Process(byte sample, float phaseOffsetRadians, float blackLevel, float syncLevel, bool colorBurstDetected, float secondAxisSign = 1f)
    {
        // Step 1: luma via a comb filter. Every sample is exactly 90
        // degrees of subcarrier phase from its neighbors (the 4x-fsc
        // assumption), so a sample 2 positions back is exactly 180
        // degrees - i.e. exactly
        // inverted - chroma, and a sample 4 positions back is a full cycle
        // (360 degrees) - i.e. same-phase - chroma. Weighting those three
        // taps 1:2:1 (X[n] + 2*X[n-2] + X[n-4]) makes the two 180-degree-
        // apart pairs (n & n-2, n-2 & n-4) cancel chroma's contribution
        // completely for a pure single-frequency chroma signal - the
        // standard 3-tap NTSC notch/comb filter - while luma (which isn't
        // oscillating at the subcarrier frequency) survives averaging
        // mostly untouched, since it barely changes sample to sample.
        // Array.Copy (and the Buffer.Memmove it delegates to) has enough fixed
        // per-call overhead that at one call per composite-video sample (millions
        // per second of emulated time) it dwarfed everything else in this method -
        // a dotnet-trace sample profile of this method in isolation showed >99% of
        // its time inside Buffer.MemmoveInternal. A manual shift of this fixed,
        // tiny (5-byte) array does the exact same thing without that call.
        _sampleHistory[4] = _sampleHistory[3];
        _sampleHistory[3] = _sampleHistory[2];
        _sampleHistory[2] = _sampleHistory[1];
        _sampleHistory[1] = _sampleHistory[0];
        _sampleHistory[0] = sample;

        var rawLuma = (_sampleHistory[0] + 2f * _sampleHistory[2] + _sampleHistory[4]) / 4f;

        // Step 2: chroma is simply whatever's left after luma is removed.
        var rawChroma = sample - rawLuma;

        // Rescale from this signal's own self-calibrated levels (see
        // SyncSeparator) onto the fixed 0-255 black-to-white scale the
        // YIQ->RGB matrix below assumes - the same rescale factor applies to
        // chroma, since chroma's amplitude lives in the same volts/byte
        // units as luma does. Reference white is reconstructed from sync
        // tip and blanking (gated-sync AGC - see TelevisionStandard.ReferenceWhiteGainFromSyncSwing),
        // not read off a running picture peak: a dim scene never contains
        // reference white, so a running-max AGC would balloon the gain.
        var whiteRef = _standard.WhiteReference(blackLevel, syncLevel);
        var scale = 255f / (whiteRef - blackLevel);
        Luma = Math.Clamp((rawLuma - blackLevel) * scale, 0, 255);
        var chroma = rawChroma * scale;

        // The color killer. A real receiver mutes its chroma demodulator on
        // any line whose back porch carried no color burst - with no phase
        // reference recovered for that line, whatever the comb filter leaves
        // behind is high-frequency luma, not color. Zeroing chroma here (and
        // still running the demod math below, so the I/Q box filter and slot
        // counter stay warm for a clean transition when burst returns) makes
        // I and Q settle to zero within one subcarrier cycle and collapses
        // the YIQ->RGB matrix to plain grayscale - the same outcome as a TV
        // whose color-killer squelch has engaged.
        if (!colorBurstDetected)
        {
            chroma = 0f;
        }

        Chroma = chroma;

        // Step 3: I/Q quadrature demodulation. Multiplying chroma by the
        // burst-locked local oscillator's in-phase/quadrature references and
        // averaging over one full subcarrier cycle isolates I and Q from
        // the 2x-subcarrier-frequency term the multiplication also
        // produces (that term averages to exactly zero over any 4
        // consecutive samples, the same trick the comb filter above uses) -
        // and, like ColorBurstPll.FinishBurstWindow's amplitude
        // recovery (which this is "the same math applied to chroma", per
        // that class's own remarks), the raw average needs multiplying by 2
        // to recover the true I/Q amplitude, not just a scaled-down
        // version of it.
        // cos/sin of the four slots' phases (base angle + a multiple of 90
        // degrees) are just sign-flipped/swapped copies of cos/sin of the
        // base angle itself - a quarter-turn rotation needs no trigonometry
        // of its own. Computing the base pair via SinCos (which shares range
        // reduction between the two, cheaper than two separate Math.Cos/
        // Math.Sin calls) and deriving the other three slots this way avoids
        // ever calling into transcendental math more than once per sample,
        // instead of the twice-per-sample this originally did - meaningful
        // since Process runs once per composite-video sample, i.e. millions
        // of times per second of emulated time.
        var slot = (int)(_sampleCounter % 4);
        var baseAngle = phaseOffsetRadians + _baseAngleOffset;
        _sampleCounter++;

        if (baseAngle != _lastBaseAngle)
        {
            (_lastBaseSin, _lastBaseCos) = MathF.SinCos(baseAngle);
            _lastBaseAngle = baseAngle;
        }

        var baseSin = _lastBaseSin;
        var baseCos = _lastBaseCos;
        var (cos, sin) = slot switch
        {
            0 => (baseCos, baseSin),
            1 => (-baseSin, baseCos),
            2 => (-baseCos, -baseSin),
            _ => (baseSin, -baseCos),
        };

        _iProductHistory[slot] = chroma * cos;
        _qProductHistory[slot] = chroma * sin;

        // Vector128<float> is always exactly 4 lanes on every platform (unlike
        // System.Numerics.Vector<float>, whose width depends on the CPU) - a
        // natural fit for summing these fixed 4-element histories.
        // Vector128.Create/Sum are cross-platform helpers that JIT-compile to
        // real SSE/NEON instructions where available and fall back to
        // equivalent scalar code otherwise, so this needs no platform guard.
        var iSum = Vector128.Sum(Vector128.Create(_iProductHistory));
        var qSum = Vector128.Sum(Vector128.Create(_qProductHistory));

        // PAL reverses the second component on alternate lines, so the line's
        // sign (ColorBurstPll.SecondAxisSign) undoes it; NTSC's is always 1.
        I = 2f * iSum / 4f;
        Q = _secondAxisPolarity * secondAxisSign * 2f * qSum / 4f;

        // Step 4: YIQ -> RGB. Real hardware does this with three resistor-
        // ratio-weighted analog summing amplifiers (the same "weighted sum"
        // pattern AppleIISystem.CompositeVideo.cs's Q3 encoder stage uses,
        // just run in reverse) - a fixed linear transform, not a lookup
        // table. Coefficients are the standard NTSC/FCC-derived matrix
        // (independently confirmed via MATLAB's ntsc2rgb and the classic
        // FCC-derived coefficients commonly reproduced in video-engineering
        // references); different sources' coefficients drift very
        // slightly, but this set is the most commonly cited one and well
        // within this project's accuracy bar.
        //
        // R/G/B share the same Luma + coeffI*I + coeffQ*Q shape, one row of
        // the matrix per channel - a matrix-vector multiply, not three
        // unrelated scalar expressions, so it's done as two
        // Vector128.FusedMultiplyAdd calls (one lane per channel, 4th lane
        // unused) instead of 6 scalar multiplies/adds, with the 0-255 clamp
        // similarly done once across all three lanes.
        var rgb = Vector128.FusedMultiplyAdd(_rgbCoeffB, Vector128.Create(Q),
            Vector128.FusedMultiplyAdd(_rgbCoeffA, Vector128.Create(I), Vector128.Create(Luma)));
        var clamped = Vector128.Clamp(rgb, Vector128<float>.Zero, Vector128.Create(255f));

        Rgb = new RgbaByte((byte)clamped[0], (byte)clamped[1], (byte)clamped[2], 255);
    }
}
