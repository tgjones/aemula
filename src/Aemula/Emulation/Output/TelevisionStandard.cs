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
        referenceWhiteGainFromSyncSwing: 2.5f); // 100 IRE picture / 40 IRE sync

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
        referenceWhiteGainFromSyncSwing: 7f / 3f);

    private TelevisionStandard(
        string name,
        double colorSubcarrierHz,
        float lineSeconds,
        float hSyncWidthSeconds,
        float frontPorchSeconds,
        float burstStartSeconds,
        int burstCycles,
        float linesPerField,
        float referenceWhiteGainFromSyncSwing)
    {
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
}
