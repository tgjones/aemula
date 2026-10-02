using System;

namespace Aemula.Emulation.Output;

// Which color-TV standard a composite signal is being decoded as, and the
// numbers that distinguish one from another. Nearly everything in the decode
// pipeline - sync separation, the raster oscillators, the color-burst PLL's
// phase maths, the luma/chroma split - is the same for every standard; this
// is the one place the differences live, so supporting another standard is
// data here rather than a parallel set of decode classes.
//
// Timing is held in seconds, not samples: a Television resolves it against
// whatever rate it is fed at (see TelevisionTiming).
public sealed class TelevisionStandard
{
    public static TelevisionStandard Ntsc { get; } = new(
        name: "NTSC",
        colorSubcarrierHz: 3_579_545,
        lineSeconds: 63.5e-6f,
        hSyncWidthSeconds: 4.7e-6f,
        frontPorchSeconds: 1.5e-6f,
        burstStartSeconds: 0.6e-6f,
        burstCycles: 9,
        linesPerField: 262.5f,
        referenceWhiteGainFromSyncSwing: 2.5f, // 100 IRE picture / 40 IRE sync
        burstSwingRadians: 0,
        burstToFirstAxisRadians: SpecBurstToIAxisDegrees * Math.PI / 180.0,
        rgbFromFirstAxis: (0.956f, -0.272f, -1.106f), // I
        rgbFromSecondAxis: (0.621f, -0.647f, 1.703f), // Q
        secondAxisPolarity: 1);

    // PAL (B/G, 625 lines, 50Hz). No setup pedestal, and sync is 300mV against
    // a 700mV picture rather than NTSC's 40:100 IRE, so the white reference
    // sits 7/3 of the sync swing above blanking.
    public static TelevisionStandard Pal { get; } = new(
        name: "PAL",
        colorSubcarrierHz: 4_433_618.75,
        lineSeconds: 64e-6f,
        hSyncWidthSeconds: 4.7e-6f,
        frontPorchSeconds: 1.65e-6f,
        burstStartSeconds: 0.9e-6f,
        burstCycles: 10,
        linesPerField: 312.5f,
        referenceWhiteGainFromSyncSwing: 7f / 3f,
        burstSwingRadians: Math.PI / 4,
        burstToFirstAxisRadians: Math.PI, // U sits opposite the mean burst
        rgbFromFirstAxis: (0f, -0.395f, 2.032f), // U
        rgbFromSecondAxis: (1.140f, -0.581f, 0f), // V
        secondAxisPolarity: -1);

    private TelevisionStandard(
        string name,
        double colorSubcarrierHz,
        float lineSeconds,
        float hSyncWidthSeconds,
        float frontPorchSeconds,
        float burstStartSeconds,
        int burstCycles,
        float linesPerField,
        float referenceWhiteGainFromSyncSwing,
        double burstSwingRadians,
        double burstToFirstAxisRadians,
        (float R, float G, float B) rgbFromFirstAxis,
        (float R, float G, float B) rgbFromSecondAxis,
        int secondAxisPolarity)
    {
        BurstSwingRadians = burstSwingRadians;
        BurstToFirstAxisRadians = burstToFirstAxisRadians;
        RgbFromFirstAxis = rgbFromFirstAxis;
        RgbFromSecondAxis = rgbFromSecondAxis;
        SecondAxisPolarity = secondAxisPolarity;
        Name = name;
        ColorSubcarrierHz = colorSubcarrierHz;
        LineSeconds = lineSeconds;
        HSyncWidthSeconds = hSyncWidthSeconds;
        FrontPorchSeconds = frontPorchSeconds;
        BurstStartSeconds = burstStartSeconds;
        BurstCycles = burstCycles;
        LinesPerField = linesPerField;
        ReferenceWhiteGainFromSyncSwing = referenceWhiteGainFromSyncSwing;
    }

    public string Name { get; }

    public double ColorSubcarrierHz { get; }

    // Four samples per subcarrier cycle is what the burst PLL and the chroma
    // demodulator are built around (every sample is exactly 0/90/180/270
    // degrees of subcarrier phase), so this is the rate a color-capable
    // producer feeds a Television at. A monochrome producer is free to use any
    // rate - see TelevisionTiming.
    public float SamplesPerSecond => (float)(4 * ColorSubcarrierHz);

    public float LineSeconds { get; }

    public float HSyncWidthSeconds { get; }

    public float FrontPorchSeconds { get; }

    // Measured from the HSYNC trailing edge, the start of back porch.
    public float BurstStartSeconds { get; }

    public int BurstCycles { get; }

    // A vertical sync pulse recurs once per *field*, not once per full (two
    // field, interlaced) frame, so this is a half-integer (262.5, 312.5).
    // That single value covers both a non-interlaced source (Apple II's 262
    // lines) and a genuinely interlaced one without the decoder needing to
    // know which it is looking at.
    public float LinesPerField { get; }

    // A real receiver runs gated-sync AGC: it measures the sync-tip-to-
    // blanking excursion and holds it constant, never keying off picture
    // white, which a dim scene may not contain. So reference white is
    // reconstructed from the two levels the signal always carries - sync tip
    // and blanking - as blanking + K * (blanking - sync). K is the standard's
    // picture-to-sync ratio: 100/40 for NTSC, 700/300 for PAL. On the shared
    // byte scale (sync 0, reference white 224) that puts blanking at 64 for
    // NTSC and ~67 for PAL.
    public float ReferenceWhiteGainFromSyncSwing { get; }

    public float WhiteReference(float blackLevel, float syncLevel) =>
        blackLevel + ReferenceWhiteGainFromSyncSwing * (blackLevel - syncLevel);

    // --- Chroma model ---
    //
    // Both standards encode color as two quadrature components on the
    // subcarrier, and both are demodulated the same way: correlate against two
    // references 90 degrees apart, then apply a 2-column matrix to luma. What
    // differs is where the axes sit relative to the burst, the matrix, and PAL's
    // line-by-line reversal of the second component.

    // How far the burst sits from the *mean* burst phase on a single line. NTSC
    // has none. PAL's burst is -U +/- V, so it swings +/-45 degrees about the
    // -U axis on alternate lines; that swing is also how a receiver knows which
    // line it is looking at (see ColorBurstPll).
    public double BurstSwingRadians { get; }

    public bool AlternatesSecondAxis => BurstSwingRadians != 0;

    // Angle from the (mean) burst phase to the first demodulation axis (I for
    // NTSC, U for PAL).
    public double BurstToFirstAxisRadians { get; }

    // R/G/B contribution of one unit of each demodulated component.
    public (float R, float G, float B) RgbFromFirstAxis { get; }
    public (float R, float G, float B) RgbFromSecondAxis { get; }

    // The sign of the second component as the quadrature demodulator (which
    // computes -sin of the phase error) delivers it, relative to the standard's
    // own definition of that component: +1 for NTSC's Q, -1 for PAL's V, which
    // sits a quarter turn the other side of U.
    public int SecondAxisPolarity { get; }

    // The NTSC derivation, kept as the worked example for how these angles are
    // arrived at from a standard's own definition of its axes.
    // The fixed rotation between the color-burst PLL's own phase-zero
    // reference (which ColorBurstPll locks to wherever the burst
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
    //     classic Y'UV/Y'IQ vector diagram. ChromaDecoderTests'
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
    // The spec figure above is the whole answer - there is deliberately no
    // empirical "which way round does this implementation need it" fudge on
    // top of it, and adding one would be a bug, not a calibration.
    //
    // An earlier version of this constant added a further 180 degrees,
    // justified as resolving a supposed lock-branch ambiguity in
    // ColorBurstPll's phase detector ("a squaring/Costas-style detector
    // can't tell a lock from a lock 180 degrees away"). That reasoning was
    // wrong on both counts. ColorBurstPll is not a Costas loop: it
    // correlates the incoming sample *directly* against its own cos/sin
    // references and uses the quadrature accumulation alone as its error
    // term (see that class's Process/FinishBurstWindow) - it never squares
    // the signal, and never multiplies its in-phase and quadrature arms
    // together, which is the step that actually creates a Costas loop's
    // sign ambiguity. With burst A*sin(90n + b) and reference 90n + P, that
    // loop's error is cos(b - P) and its update is P -= gain*cos(b - P), so
    // its fixed points are P = b +/- 90 degrees and only P = b - 90 is
    // *stable* (the other one diverges under the same perturbation). One
    // stable lock, reached from any starting phase, identical for every
    // signal - so there is nothing here for a per-source constant to
    // resolve, and no source-dependent branch for a "tint knob" to chase.
    //
    // What the +180 was really compensating for was a defect in the one
    // reference signal this project had at the time: smpte.ntsc transmits
    // its burst 180 degrees away from where RS-170A puts it (measured
    // directly from the asset's raw bytes - see SmpteAsset's own remarks,
    // which now corrects it at load instead). Two 180-degree errors
    // cancelling made the SMPTE bars decode correctly while leaving every
    // spec-conformant source - Atari 2600's TIA in particular, whose burst
    // and hue 1 are the same delay-line tap - decoding a full half-turn
    // around the hue circle from its real colors.
    private const double IAxisFromVAxisDegrees = 33.0;
    private const double VAxisFromUAxisDegrees = 90.0;
    private const double BurstFromUAxisDegrees = 180.0;
    private const double SpecBurstToIAxisDegrees =
        (VAxisFromUAxisDegrees + IAxisFromVAxisDegrees) - BurstFromUAxisDegrees; // -57


}
