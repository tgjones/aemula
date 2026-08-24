using System;
using System.Threading.Tasks;
using Aemula.Emulation.Output.Ntsc;

namespace Aemula.Tests.Emulation.Output.Ntsc;

// Isolated, synthetic-signal tests for NtscYiqDecoder: full smpte.ntsc
// property assertions belong in TelevisionTests, focused per-class math
// checks belong here.
public class NtscYiqDecoderTests
{
    // Both the comb filter and the I/Q box-average are 4-sample rolling
    // windows seeded with zeros, so their very first few outputs are still
    // warming up rather than steady-state - every test below skips this
    // many samples before asserting.
    private const int WarmupSamples = 8;

    // Builds a synthetic sample exactly the way NtscYiqDecoder.Process's own
    // internal phase formula expects it: rawLuma plus a sinusoid at the
    // decoder's own reference phase (slot*90 degrees + phaseOffsetRadians +
    // its internal burst-to-I-axis rotation), offset by an extra
    // caller-chosen angle. Because this generates chroma using the *same*
    // phase formula Process uses internally, the amplitude/angle predicted
    // by the derivation in NtscYiqDecoder's own remarks (I = amplitude *
    // sin(extraAngle), Q = amplitude * cos(extraAngle) once the box filter
    // is warmed up) can be checked directly - the same "generate against the
    // decoder's own reference" approach NtscColorBurstPllTests already uses
    // for its synthetic burst.
    private static byte BuildSample(int sampleIndex, float rawLuma, float amplitude, float extraAngle)
    {
        var slot = sampleIndex % 4;
        var phase = Math.PI / 2.0 * slot + NtscYiqDecoder.BurstToIAxisRotationRadians + extraAngle;
        return (byte)Math.Clamp(Math.Round(rawLuma + amplitude * Math.Sin(phase)), 0, 255);
    }

    [Test]
    public async Task CombFilterRecoversConstantLumaWithNoChroma()
    {
        var decoder = new NtscYiqDecoder();

        for (var i = 0; i < 40; i++)
        {
            decoder.Process(150, phaseOffsetRadians: 0, blackLevel: 0, whiteLevel: 255);

            if (i >= WarmupSamples)
            {
                await Assert.That(decoder.Luma).IsBetween(149.99f, 150.01f);
                await Assert.That(decoder.I).IsBetween(-0.01f, 0.01f);
                await Assert.That(decoder.Q).IsBetween(-0.01f, 0.01f);
            }
        }
    }

    [Test]
    public async Task DemodulatesChromaAlignedWithQAxis()
    {
        // extraAngle = 0 means the chroma this generates sits exactly on
        // the decoder's own internal reference phase (no additional
        // rotation) - per the sin(extraAngle)/cos(extraAngle) identity in
        // BuildSample's remarks, that predicts I = 0, Q = amplitude.
        const float rawLuma = 120;
        const float amplitude = 30;

        var decoder = new NtscYiqDecoder();

        for (var i = 0; i < 40; i++)
        {
            var sample = BuildSample(i, rawLuma, amplitude, extraAngle: 0);
            decoder.Process(sample, phaseOffsetRadians: 0, blackLevel: 0, whiteLevel: 255);

            if (i >= WarmupSamples)
            {
                await Assert.That(decoder.Luma).IsBetween(rawLuma - 0.5f, rawLuma + 0.5f);
                await Assert.That(decoder.I).IsBetween(-0.5f, 0.5f);
                await Assert.That(decoder.Q).IsBetween(amplitude - 0.5f, amplitude + 0.5f);
            }
        }
    }

    [Test]
    public async Task DemodulatesChromaAlignedWithIAxis()
    {
        // extraAngle = pi/2 predicts I = amplitude, Q = 0 - see BuildSample.
        const float rawLuma = 120;
        const float amplitude = 30;

        var decoder = new NtscYiqDecoder();

        for (var i = 0; i < 40; i++)
        {
            var sample = BuildSample(i, rawLuma, amplitude, extraAngle: MathF.PI / 2f);
            decoder.Process(sample, phaseOffsetRadians: 0, blackLevel: 0, whiteLevel: 255);

            if (i >= WarmupSamples)
            {
                await Assert.That(decoder.I).IsBetween(amplitude - 0.5f, amplitude + 0.5f);
                await Assert.That(decoder.Q).IsBetween(-0.5f, 0.5f);
            }
        }
    }

    [Test]
    public async Task DemodulatesArbitraryChromaAngle()
    {
        // A non-axis-aligned angle exercises the general sin/cos split
        // rather than either of the two convenient special cases above.
        const float rawLuma = 100;
        const float amplitude = 40;
        const float extraAngle = 0.9f; // arbitrary, deliberately not a multiple of pi/2

        var expectedI = amplitude * MathF.Sin(extraAngle);
        var expectedQ = amplitude * MathF.Cos(extraAngle);

        var decoder = new NtscYiqDecoder();

        for (var i = 0; i < 40; i++)
        {
            var sample = BuildSample(i, rawLuma, amplitude, extraAngle);
            decoder.Process(sample, phaseOffsetRadians: 0, blackLevel: 0, whiteLevel: 255);

            if (i >= WarmupSamples)
            {
                await Assert.That(decoder.I).IsBetween(expectedI - 0.5f, expectedI + 0.5f);
                await Assert.That(decoder.Q).IsBetween(expectedQ - 0.5f, expectedQ + 0.5f);
            }
        }
    }

    [Test]
    public async Task NoChromaProducesGrayscaleRgb()
    {
        var decoder = new NtscYiqDecoder();

        for (var i = 0; i < 40; i++)
        {
            decoder.Process(200, phaseOffsetRadians: 0, blackLevel: 0, whiteLevel: 255);
        }

        // I = Q = 0 collapses the YIQ->RGB matrix to R = G = B = Y for every
        // set of coefficients, regardless of their exact values - a
        // sanity check on the matrix's shape, not its specific weights.
        await Assert.That(decoder.Rgb.R).IsEqualTo(decoder.Rgb.G);
        await Assert.That(decoder.Rgb.G).IsEqualTo(decoder.Rgb.B);
        await Assert.That((int)decoder.Rgb.R).IsBetween(198, 202);
    }

    [Test]
    public async Task MatrixMatchesStandardYiqToRgbCoefficients()
    {
        // I-axis-aligned chroma (see DemodulatesChromaAlignedWithIAxis)
        // gives I = amplitude, Q = 0 once warmed up, letting the R/G/B
        // outputs be checked directly against the standard YIQ-to-RGB
        // coefficients: R = Y + 0.956I, G = Y - 0.272I, B = Y - 1.106I.
        const float rawLuma = 128;
        const float amplitude = 40;

        var decoder = new NtscYiqDecoder();

        for (var i = 0; i < 40; i++)
        {
            var sample = BuildSample(i, rawLuma, amplitude, extraAngle: MathF.PI / 2f);
            decoder.Process(sample, phaseOffsetRadians: 0, blackLevel: 0, whiteLevel: 255);
        }

        var expectedR = Math.Clamp(rawLuma + 0.956 * amplitude, 0, 255);
        var expectedG = Math.Clamp(rawLuma - 0.272 * amplitude, 0, 255);
        var expectedB = Math.Clamp(rawLuma - 1.106 * amplitude, 0, 255);

        await Assert.That((int)decoder.Rgb.R).IsBetween((int)expectedR - 2, (int)expectedR + 2);
        await Assert.That((int)decoder.Rgb.G).IsBetween((int)expectedG - 2, (int)expectedG + 2);
        await Assert.That((int)decoder.Rgb.B).IsBetween((int)expectedB - 2, (int)expectedB + 2);
    }

    [Test]
    public async Task RescalesUsingBlackAndWhiteLevelsNotRawByteRange()
    {
        // blackLevel/whiteLevel narrower than the full 0-255 byte range (the
        // normal case for a real signal - see NtscSyncSeparator) should
        // rescale Luma up to the full 0-255 output range, not leave it
        // sitting wherever it fell on the raw byte scale.
        const float blackLevel = 64;
        const float whiteLevel = 191; // narrower swing than 0-255

        var decoder = new NtscYiqDecoder();

        // Raw byte 191 is the signal's own white level, i.e. should decode
        // to a fully-white 255 Luma once rescaled.
        for (var i = 0; i < 10; i++)
        {
            decoder.Process(191, phaseOffsetRadians: 0, blackLevel, whiteLevel);
        }

        await Assert.That(decoder.Luma).IsBetween(254.0f, 255.0f);
    }

    // This isn't a test of NtscYiqDecoder.Process at all - it's a check on
    // the *derivation* behind NtscYiqDecoder.BurstToIAxisRotationRadians
    // (see that constant's own remarks): that I/Q really are just the
    // standard Y'UV plane's U/V axes rotated by 33 degrees, not merely a
    // number that happens to be close. It reconstructs the well-known
    // Y'IQ matrix (0.596/-0.274/-0.322 for I, 0.211/-0.523/0.312 for Q -
    // commonly reproduced in video-engineering references, e.g. the
    // Wikipedia YIQ article) from first principles - Y'UV's own coefficients
    // (0.492/0.877) plus a 33-degree rotation - and checks they agree. If
    // this ever failed, it would mean the 33-degree figure the burst-to-I-
    // axis derivation depends on doesn't actually reproduce the standard
    // matrix, i.e. the derivation itself would be suspect - not just this
    // test.
    [Test]
    [Arguments(1.0, 0.0, 0.0)]
    [Arguments(0.0, 1.0, 0.0)]
    [Arguments(0.0, 0.0, 1.0)]
    [Arguments(0.6, 0.3, 0.9)]
    public async Task ThirtyThreeDegreeUvRotationReproducesStandardYiqCoefficients(double r, double g, double b)
    {
        const double iAxisFromVAxisDegrees = 33.0;
        var iAxisFromUAxisRadians = (90.0 + iAxisFromVAxisDegrees) * Math.PI / 180.0;
        var qAxisFromUAxisRadians = iAxisFromVAxisDegrees * Math.PI / 180.0;

        var y = 0.299 * r + 0.587 * g + 0.114 * b;
        var u = 0.492 * (b - y);
        var v = 0.877 * (r - y);

        var i = u * Math.Cos(iAxisFromUAxisRadians) + v * Math.Sin(iAxisFromUAxisRadians);
        var q = u * Math.Cos(qAxisFromUAxisRadians) + v * Math.Sin(qAxisFromUAxisRadians);

        var expectedI = 0.596 * r - 0.274 * g - 0.322 * b;
        var expectedQ = 0.211 * r - 0.523 * g + 0.312 * b;

        await Assert.That(i).IsBetween(expectedI - 0.005, expectedI + 0.005);
        await Assert.That(q).IsBetween(expectedQ - 0.005, expectedQ + 0.005);
    }
}
