using System.Threading.Tasks;
using Aemula.Emulation.Output;
using Aemula.Emulation.Output.Ntsc;

namespace Aemula.Tests.Emulation.Output;

public class TelevisionTests
{
    [Test]
    public async Task SmpteAssetNormalizesToFullByteRange()
    {
        var normalized = SmpteAsset.LoadNormalized();

        // 955,500 bytes at 910 samples/line (63.5µs at exactly 4x the NTSC
        // color subcarrier) is exactly 1050 lines, i.e. 525 lines x 2 fields.
        await Assert.That(normalized.Length).IsEqualTo(955_500);

        // The raw asset's actual range is [4, 199] (confirmed by inspection,
        // not assumed) - rescaled by *224/200 (so the 100 IRE white bar at
        // raw 200 lands on reference white 224) with integer truncation,
        // that becomes [4, 222]: 4*224/200 = 4.48 -> 4, 199*224/200 =
        // 222.88 -> 222. Asserting the exact rescaled extremes (rather than
        // just "some values changed") catches an off-by-one in the rescale
        // formula, not just its general direction.
        var min = normalized[0];
        var max = normalized[0];

        foreach (var sample in normalized)
        {
            if (sample < min) min = sample;
            if (sample > max) max = sample;
        }

        await Assert.That(min).IsEqualTo((byte)4);
        await Assert.That(max).IsEqualTo((byte)222);
    }

    // smpte.ntsc encodes the classic SMPTE 75% color-bar test pattern - a
    // top strip of 7 equal-width solid vertical bars, in a fixed,
    // well-known order: white, yellow, cyan, green, magenta, red, blue
    // (left to right). This test decodes the whole asset through the real
    // Television front door - sync separation, raster oscillators, burst
    // PLL, and YIQ decode, exactly as a real caller would - then checks that
    // the seven bars actually come out in that hue order and with the
    // correct relative brightness (white brightest, blue darkest, matching
    // the standard 75%-bars luma progression), with generous tolerances,
    // since this project's accuracy bar is "recognizably correct", not
    // broadcast-accurate colorimetry.
    [Test]
    public async Task DecodesSmpteColorBarsInExpectedHueAndLumaOrder()
    {
        var samples = SmpteAsset.LoadNormalized();
        var television = new Television();

        foreach (var sample in samples)
        {
            television.Decode(sample);
        }

        var buffer = television.SampleBuffer;

        // Any row comfortably within the top two-thirds of the frame shows
        // the clean 7-bar strip for this asset (confirmed by inspection);
        // the bottom third carries a different sub-pattern (-I/white/+Q
        // strip, PLUGE) that this test doesn't check.
        var row = (int)(buffer.Height / 6);

        // Each of the 7 bars is an equal fraction of the active-video
        // width, offset by where active video actually starts within the
        // line - Television.Decode writes every sample at its true raster
        // column, so column 0 in the buffer is the start of the line
        // (sync/blanking), not the start of the picture. Sampling at
        // the middle of each bar keeps well clear of the transition columns
        // between bars.
        var barWidth = NtscTiming.ActiveVideoLengthSamples / 7.0;

        RgbaByte SampleBar(int barIndex)
        {
            var column = (int)(NtscTiming.ActiveVideoStartSamples + (barIndex + 0.5) * barWidth);
            return buffer.Data[row * buffer.Width + column].Color;
        }

        var white = SampleBar(0);
        var yellow = SampleBar(1);
        var cyan = SampleBar(2);
        var green = SampleBar(3);
        var magenta = SampleBar(4);
        var red = SampleBar(5);
        var blue = SampleBar(6);

        // Hue checks: each bar's defining channel relationship, not exact
        // values - e.g. yellow is "red and green both clearly outweigh
        // blue", not a specific RGB triple.
        await Assert.That(white.R).IsGreaterThan((byte)150);
        await Assert.That(IsRoughlyEqual(white.R, white.G)).IsTrue();
        await Assert.That(IsRoughlyEqual(white.G, white.B)).IsTrue();

        await Assert.That(yellow.R > yellow.B + 50).IsTrue();
        await Assert.That(yellow.G > yellow.B + 50).IsTrue();

        await Assert.That(cyan.G > cyan.R + 50).IsTrue();
        await Assert.That(cyan.B > cyan.R + 50).IsTrue();

        await Assert.That(green.G > green.R + 50).IsTrue();
        await Assert.That(green.G > green.B + 50).IsTrue();

        await Assert.That(magenta.R > magenta.G + 50).IsTrue();
        await Assert.That(magenta.B > magenta.G + 50).IsTrue();

        await Assert.That(red.R > red.G + 50).IsTrue();
        await Assert.That(red.R > red.B + 50).IsTrue();

        await Assert.That(blue.B > blue.R + 50).IsTrue();
        await Assert.That(blue.B > blue.G + 50).IsTrue();

        // Luma ordering: the standard 75%-bars progression, brightest to
        // darkest.
        double Luma(RgbaByte c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;

        var lumas = new[] { Luma(white), Luma(yellow), Luma(cyan), Luma(green), Luma(magenta), Luma(red), Luma(blue) };

        for (var i = 0; i < lumas.Length - 1; i++)
        {
            await Assert.That(lumas[i]).IsGreaterThan(lumas[i + 1]);
        }
    }

    private static bool IsRoughlyEqual(byte a, byte b) => System.Math.Abs(a - b) < 20;
}
