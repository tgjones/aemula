using System;
using System.Threading.Tasks;
using Aemula.Emulation.Output.Ntsc;

namespace Aemula.Tests.Emulation.Output.Ntsc;

// Isolated, synthetic-signal tests for NtscYiqDecoder - see the plan doc's
// Testing section: full smpte.ntsc property assertions belong in
// TelevisionTests, focused per-class math checks belong here.
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
    private static byte BuildSample(int sampleIndex, double rawLuma, double amplitude, double extraAngle)
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
                await Assert.That(decoder.Luma).IsBetween(149.99, 150.01);
                await Assert.That(decoder.I).IsBetween(-0.01, 0.01);
                await Assert.That(decoder.Q).IsBetween(-0.01, 0.01);
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
        const double rawLuma = 120;
        const double amplitude = 30;

        var decoder = new NtscYiqDecoder();

        for (var i = 0; i < 40; i++)
        {
            var sample = BuildSample(i, rawLuma, amplitude, extraAngle: 0);
            decoder.Process(sample, phaseOffsetRadians: 0, blackLevel: 0, whiteLevel: 255);

            if (i >= WarmupSamples)
            {
                await Assert.That(decoder.Luma).IsBetween(rawLuma - 0.5, rawLuma + 0.5);
                await Assert.That(decoder.I).IsBetween(-0.5, 0.5);
                await Assert.That(decoder.Q).IsBetween(amplitude - 0.5, amplitude + 0.5);
            }
        }
    }

    [Test]
    public async Task DemodulatesChromaAlignedWithIAxis()
    {
        // extraAngle = pi/2 predicts I = amplitude, Q = 0 - see BuildSample.
        const double rawLuma = 120;
        const double amplitude = 30;

        var decoder = new NtscYiqDecoder();

        for (var i = 0; i < 40; i++)
        {
            var sample = BuildSample(i, rawLuma, amplitude, extraAngle: Math.PI / 2.0);
            decoder.Process(sample, phaseOffsetRadians: 0, blackLevel: 0, whiteLevel: 255);

            if (i >= WarmupSamples)
            {
                await Assert.That(decoder.I).IsBetween(amplitude - 0.5, amplitude + 0.5);
                await Assert.That(decoder.Q).IsBetween(-0.5, 0.5);
            }
        }
    }

    [Test]
    public async Task DemodulatesArbitraryChromaAngle()
    {
        // A non-axis-aligned angle exercises the general sin/cos split
        // rather than either of the two convenient special cases above.
        const double rawLuma = 100;
        const double amplitude = 40;
        const double extraAngle = 0.9; // arbitrary, deliberately not a multiple of pi/2

        var expectedI = amplitude * Math.Sin(extraAngle);
        var expectedQ = amplitude * Math.Cos(extraAngle);

        var decoder = new NtscYiqDecoder();

        for (var i = 0; i < 40; i++)
        {
            var sample = BuildSample(i, rawLuma, amplitude, extraAngle);
            decoder.Process(sample, phaseOffsetRadians: 0, blackLevel: 0, whiteLevel: 255);

            if (i >= WarmupSamples)
            {
                await Assert.That(decoder.I).IsBetween(expectedI - 0.5, expectedI + 0.5);
                await Assert.That(decoder.Q).IsBetween(expectedQ - 0.5, expectedQ + 0.5);
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
        // outputs be checked directly against the plan doc's cited
        // coefficients: R = Y + 0.956I, G = Y - 0.272I, B = Y - 1.106I.
        const double rawLuma = 128;
        const double amplitude = 40;

        var decoder = new NtscYiqDecoder();

        for (var i = 0; i < 40; i++)
        {
            var sample = BuildSample(i, rawLuma, amplitude, extraAngle: Math.PI / 2.0);
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
        const double blackLevel = 64;
        const double whiteLevel = 191; // narrower swing than 0-255

        var decoder = new NtscYiqDecoder();

        // Raw byte 191 is the signal's own white level, i.e. should decode
        // to a fully-white 255 Luma once rescaled.
        for (var i = 0; i < 10; i++)
        {
            decoder.Process(191, phaseOffsetRadians: 0, blackLevel, whiteLevel);
        }

        await Assert.That(decoder.Luma).IsBetween(254.0, 255.0);
    }
}
