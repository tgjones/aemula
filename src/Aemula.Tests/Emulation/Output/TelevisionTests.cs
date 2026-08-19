using System.Threading.Tasks;

namespace Aemula.Tests.Emulation.Output;

// This test namespace nests under the root Aemula namespace, where the
// older, unrelated Aemula.Television already lives (see the plan doc's
// "Naming collision, explicitly out of scope" note). A using-alias placed
// above the namespace declaration is compilation-unit-scoped, which loses
// to that ancestor-namespace member during plain-name lookup - placing it
// here, inside the namespace body, is what actually gives it priority.
using Television = Aemula.Emulation.Output.Television;
using Aemula.Emulation.Output.Ntsc;

// Phase 0 of docs/television-plan.md. This file replaces an earlier
// [Skip]ped prototype (System.Drawing.Bitmap-based, didn't run on CI) - see
// the plan doc's "Testing" section for why it wasn't extended instead.
public class TelevisionTests
{
    [Test]
    public async Task SmpteAssetNormalizesToFullByteRange()
    {
        var normalized = SmpteAsset.LoadNormalized();

        // 955,500 bytes at 910 samples/line (63.5µs at exactly 4x the NTSC
        // color subcarrier) is exactly 1050 lines, i.e. 525 lines x 2 fields
        // - see the plan doc's "Existing state" section.
        await Assert.That(normalized.Length).IsEqualTo(955_500);

        // The raw asset's actual range is [4, 199] (confirmed by inspection,
        // not assumed) - rescaled by *255/200 with integer truncation, that
        // becomes [5, 253]. Asserting the exact rescaled extremes (rather
        // than just "some values changed") catches an off-by-one in the
        // rescale formula, not just its general direction.
        var min = normalized[0];
        var max = normalized[0];

        foreach (var sample in normalized)
        {
            if (sample < min) min = sample;
            if (sample > max) max = sample;
        }

        await Assert.That(min).IsEqualTo((byte)5);
        await Assert.That(max).IsEqualTo((byte)253);
    }

    // The "Done when" check for Phase 4: smpte.ntsc encodes the classic
    // SMPTE 75% color-bar test pattern - a top strip of 7 equal-width solid
    // vertical bars, in a fixed, well-known order: white, yellow, cyan,
    // green, magenta, red, blue (left to right; see the plan doc's Testing
    // section). This test decodes the whole asset through the real
    // Television front door - sync separation, raster oscillators, burst
    // PLL, and YIQ decode, exactly as a real caller would - then checks that
    // the seven bars actually come out in that hue order and with the
    // correct relative brightness (white brightest, blue darkest, matching
    // the standard 75%-bars luma progression), with generous tolerances -
    // per the plan doc, this project's accuracy bar is "recognizably
    // correct", not broadcast-accurate colorimetry.
    [Test]
    public async Task DecodesSmpteColorBarsInExpectedHueAndLumaOrder()
    {
        var samples = SmpteAsset.LoadNormalized();
        var television = new Television();

        foreach (var sample in samples)
        {
            television.Decode(sample);
        }

        var buffer = television.DisplayBuffer;

        // Any row comfortably within the top two-thirds of the frame shows
        // the clean 7-bar strip for this asset (confirmed by inspection);
        // the bottom third carries a different sub-pattern (-I/white/+Q
        // strip, PLUGE) the plan doc doesn't ask this test to check.
        var row = (int)(buffer.Height / 6);

        // Each of the 7 bars is an equal fraction of the active-video
        // width (not the full buffer width - Television.Decode only writes
        // into the active-video portion of each row, see its remarks on
        // IsActiveVideo; the rest of the line's samples, sync/blanking,
        // never get written and stay at DisplayBuffer's initial black).
        // Sampling at the middle of each bar keeps well clear of the
        // transition columns between bars.
        var barWidth = NtscTiming.ActiveVideoLengthSamples / 7.0;

        RgbaByte SampleBar(int barIndex)
        {
            var column = (int)((barIndex + 0.5) * barWidth);
            return buffer.Data[row * buffer.Width + column];
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
