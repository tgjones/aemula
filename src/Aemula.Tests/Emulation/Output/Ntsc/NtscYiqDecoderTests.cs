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

    // Every Process call below feeds a realistic spec-scale separation -
    // sync tip 0, black 64 - not the degenerate black 0 / white 255 an
    // earlier version used. The decoder now reconstructs its gain as
    // 255 / (whiteRef - blackLevel), whiteRef = blackLevel + 2.5 *
    // (blackLevel - syncLevel) (2.5 = 100 IRE picture / 40 IRE
    // sync-to-blanking), so a zero sync-to-black swing would divide by
    // zero. With black 64 and sync 0, whiteRef lands on reference white
    // 224 and one picture byte rescales by 255 / (224 - 64) = 1.59375.
    private const float SyncLevel = 0f;
    private const float BlackLevel = 64f;
    private const float DecodeScale = 255f / (224f - 64f);

    // Builds a synthetic sample exactly the way NtscYiqDecoder.Process's own
    // internal phase formula expects it: rawLuma plus a sinusoid at the
    // decoder's own reference phase (slot*90 degrees + phaseOffsetRadians +
    // its internal burst-to-I-axis rotation), offset by an extra
    // caller-chosen angle. Because this generates chroma using the *same*
    // phase formula Process uses internally, the amplitude/angle predicted
    // by the derivation in NtscYiqDecoder's own remarks (I = amplitude *
    // sin(extraAngle), Q = amplitude * cos(extraAngle) once the box filter
    // is warmed up, both then scaled by DecodeScale) can be checked
    // directly - the same "generate against the decoder's own reference"
    // approach NtscColorBurstPllTests already uses for its synthetic burst.
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

        // Raw byte 150 sits (150 - 64) above black, so after the sync-
        // anchored rescale it decodes to 86 * 1.59375.
        var expectedLuma = (150f - BlackLevel) * DecodeScale;

        for (var i = 0; i < 40; i++)
        {
            decoder.Process(150, phaseOffsetRadians: 0, blackLevel: BlackLevel, syncLevel: SyncLevel);

            if (i >= WarmupSamples)
            {
                await Assert.That(decoder.Luma).IsBetween(expectedLuma - 0.5f, expectedLuma + 0.5f);
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
        // BuildSample's remarks, that predicts I = 0, Q = amplitude, both
        // then scaled by DecodeScale.
        const float rawLuma = 120;
        const float amplitude = 30;

        var expectedLuma = (rawLuma - BlackLevel) * DecodeScale;
        var expectedQ = amplitude * DecodeScale;

        var decoder = new NtscYiqDecoder();

        for (var i = 0; i < 40; i++)
        {
            var sample = BuildSample(i, rawLuma, amplitude, extraAngle: 0);
            decoder.Process(sample, phaseOffsetRadians: 0, blackLevel: BlackLevel, syncLevel: SyncLevel);

            if (i >= WarmupSamples)
            {
                await Assert.That(decoder.Luma).IsBetween(expectedLuma - 1f, expectedLuma + 1f);
                await Assert.That(decoder.I).IsBetween(-1f, 1f);
                await Assert.That(decoder.Q).IsBetween(expectedQ - 1f, expectedQ + 1f);
            }
        }
    }

    [Test]
    public async Task DemodulatesChromaAlignedWithIAxis()
    {
        // extraAngle = pi/2 predicts I = amplitude, Q = 0 (scaled by
        // DecodeScale) - see BuildSample.
        const float rawLuma = 120;
        const float amplitude = 30;

        var expectedI = amplitude * DecodeScale;

        var decoder = new NtscYiqDecoder();

        for (var i = 0; i < 40; i++)
        {
            var sample = BuildSample(i, rawLuma, amplitude, extraAngle: MathF.PI / 2f);
            decoder.Process(sample, phaseOffsetRadians: 0, blackLevel: BlackLevel, syncLevel: SyncLevel);

            if (i >= WarmupSamples)
            {
                await Assert.That(decoder.I).IsBetween(expectedI - 1f, expectedI + 1f);
                await Assert.That(decoder.Q).IsBetween(-1f, 1f);
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

        var expectedI = amplitude * DecodeScale * MathF.Sin(extraAngle);
        var expectedQ = amplitude * DecodeScale * MathF.Cos(extraAngle);

        var decoder = new NtscYiqDecoder();

        for (var i = 0; i < 40; i++)
        {
            var sample = BuildSample(i, rawLuma, amplitude, extraAngle);
            decoder.Process(sample, phaseOffsetRadians: 0, blackLevel: BlackLevel, syncLevel: SyncLevel);

            if (i >= WarmupSamples)
            {
                await Assert.That(decoder.I).IsBetween(expectedI - 1f, expectedI + 1f);
                await Assert.That(decoder.Q).IsBetween(expectedQ - 1f, expectedQ + 1f);
            }
        }
    }

    [Test]
    public async Task NoChromaProducesGrayscaleRgb()
    {
        var decoder = new NtscYiqDecoder();

        for (var i = 0; i < 40; i++)
        {
            decoder.Process(200, phaseOffsetRadians: 0, blackLevel: BlackLevel, syncLevel: SyncLevel);
        }

        // I = Q = 0 collapses the YIQ->RGB matrix to R = G = B = Y for every
        // set of coefficients, regardless of their exact values - a
        // sanity check on the matrix's shape, not its specific weights.
        // Raw byte 200 decodes to (200 - 64) * 1.59375 = 216.75.
        var expectedLuma = (int)MathF.Round((200f - BlackLevel) * DecodeScale);

        await Assert.That(decoder.Rgb.R).IsEqualTo(decoder.Rgb.G);
        await Assert.That(decoder.Rgb.G).IsEqualTo(decoder.Rgb.B);
        await Assert.That((int)decoder.Rgb.R).IsBetween(expectedLuma - 2, expectedLuma + 2);
    }

    [Test]
    public async Task MatrixMatchesStandardYiqToRgbCoefficients()
    {
        // I-axis-aligned chroma (see DemodulatesChromaAlignedWithIAxis)
        // gives I = amplitude, Q = 0 once warmed up (both scaled by
        // DecodeScale), letting the R/G/B outputs be checked directly
        // against the standard YIQ-to-RGB coefficients: R = Y + 0.956I,
        // G = Y - 0.272I, B = Y - 1.106I.
        const float rawLuma = 128;
        const float amplitude = 40;

        var decoder = new NtscYiqDecoder();

        for (var i = 0; i < 40; i++)
        {
            var sample = BuildSample(i, rawLuma, amplitude, extraAngle: MathF.PI / 2f);
            decoder.Process(sample, phaseOffsetRadians: 0, blackLevel: BlackLevel, syncLevel: SyncLevel);
        }

        var decodedLuma = (rawLuma - BlackLevel) * DecodeScale;
        var decodedI = amplitude * DecodeScale;

        var expectedR = Math.Clamp(decodedLuma + 0.956 * decodedI, 0, 255);
        var expectedG = Math.Clamp(decodedLuma - 0.272 * decodedI, 0, 255);
        var expectedB = Math.Clamp(decodedLuma - 1.106 * decodedI, 0, 255);

        await Assert.That((int)decoder.Rgb.R).IsBetween((int)expectedR - 2, (int)expectedR + 2);
        await Assert.That((int)decoder.Rgb.G).IsBetween((int)expectedG - 2, (int)expectedG + 2);
        await Assert.That((int)decoder.Rgb.B).IsBetween((int)expectedB - 2, (int)expectedB + 2);
    }

    [Test]
    public async Task MidGreyDecodesToSpecPredictedLumaIndependentOfReferenceWhitePresence()
    {
        // The decoder's gain is reconstructed from sync tip and blanking
        // alone (whiteRef = blackLevel + 2.5*(blackLevel - syncLevel)),
        // never from a running picture peak - so a mid-grey sample decodes
        // to the fixed value the spec formula predicts, (midGrey - 64) *
        // 1.59375, whether or not the same line also carried a
        // reference-white (224) sample. That invariance is the dim-scene
        // gain-stability guarantee (a forest, a night sky: no reference
        // white anywhere in frame) checked at the decoder level; the same
        // property end-to-end through NtscSyncSeparator's level tracking is
        // a Television-level test.
        const byte MidGrey = 144;
        const byte ReferenceWhite = 224;

        var expectedLuma = (MidGrey - BlackLevel) * DecodeScale; // 80 * 1.59375 = 127.5

        var greyOnly = new NtscYiqDecoder();
        for (var i = 0; i < 40; i++)
        {
            greyOnly.Process(MidGrey, phaseOffsetRadians: 0, blackLevel: BlackLevel, syncLevel: SyncLevel);
        }

        // Reference white first, then the same mid-grey run - warmed well
        // past the 8-sample comb/box-filter window so no residue of the
        // white samples remains in either filter.
        var withWhite = new NtscYiqDecoder();
        for (var i = 0; i < 20; i++)
        {
            withWhite.Process(ReferenceWhite, phaseOffsetRadians: 0, blackLevel: BlackLevel, syncLevel: SyncLevel);
        }
        for (var i = 0; i < 40; i++)
        {
            withWhite.Process(MidGrey, phaseOffsetRadians: 0, blackLevel: BlackLevel, syncLevel: SyncLevel);
        }

        await Assert.That(greyOnly.Luma).IsBetween(expectedLuma - 0.5f, expectedLuma + 0.5f);
        await Assert.That(withWhite.Luma).IsBetween(expectedLuma - 0.5f, expectedLuma + 0.5f);
        await Assert.That(withWhite.Luma).IsEqualTo(greyOnly.Luma);
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
