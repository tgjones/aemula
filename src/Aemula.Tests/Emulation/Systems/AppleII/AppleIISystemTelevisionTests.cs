using System;
using System.Threading.Tasks;
using Aemula.Emulation.Output;
using Aemula.Emulation.Systems.AppleII;

namespace Aemula.Tests.Emulation.Systems.AppleII;

// The code-verified replacement for "eyeball the Television window and hope
// it looks right" - pokes a HIRES
// byte pattern with a documented expected NTSC artifact color (from Jim
// Sather's "Understanding the Apple II", p.8-15/8-16 - see below) into
// screen memory, runs a frame, and checks the resulting pixels in
// AppleIISystem.Television.SampleBuffer (fed live from
// AppleIISystem.TickCompositeVideo - see AppleIISystem.CompositeVideo.cs)
// actually decode to that color.
public class AppleIISystemTelevisionTests
{
    // Same boot/frame budgets AppleIISystemVideoModesTests already
    // establishes: 500,000 ticks gets the Autostart ROM through its boot
    // sequence and into its idle loop (screen-mode soft switches must be
    // poked after boot, or the ROM's own TEXT-mode setup stomps them), and
    // 400,000 more comfortably covers a full ~238,000-tick frame. By the
    // time boot alone finishes, AppleIISystem.Television has already
    // processed hundreds of thousands of live composite-video samples, so
    // its self-calibrating sync/level tracking, raster oscillators, and
    // burst PLL are already locked well before the pattern under test is
    // even poked in.
    private static void BootToIdle(AppleIISystem system)
    {
        // These tests read Sample.Region back out of SampleBuffer, which
        // Television only populates when asked to.
        system.Television.CaptureSampleDiagnostics = true;

        for (var i = 0; i < 500_000; i++)
        {
            system.Tick();
        }
    }

    private static void TickOneFrame(AppleIISystem system)
    {
        for (var i = 0; i < 400_000; i++)
        {
            system.Tick();
        }
    }

    // Sather p.8-15: "A fact of life, when dealing with a 1 MHz video cycle
    // and a 3.5 MHz COLOR REFERENCE, is that there are 3.5 COLOR
    // REFERENCE cycles per video cycle. The result is that the COLOR
    // REFERENCE begins every cycle 180 degrees out of phase from the way
    // it was on the previous video cycle... identical dot patterns produce
    // colors 180 degrees out of phase in adjacent video cycles... the
    // programmer must process even memory locations different than odd
    // memory locations when producing colored HIRES displays. As an
    // example, to produce a short green line, 00101010 is stored at an
    // even address or 01010101 is stored at an odd address."
    //
    // Filling *every* even HIRES byte with one of these two patterns and
    // every odd byte with the other reproduces Sather's worked example
    // across the entire screen at once (memory address parity, not the
    // screen row/column a byte's address scrambles to, is what determines
    // which of the pattern's two forms belongs at that address - see
    // DrawHiresByte's own remarks on bit 0 being the leftmost of the 7
    // dots), without needing to compute the HIRES address-scrambling
    // formula for any particular row.
    private const byte GreenPatternForEvenAddress = 0b0010_1010; // $2A
    private const byte GreenPatternForOddAddress = 0b0101_0101; // $55

    // Sather p.8-15, same paragraph: "identical dot patterns produce
    // colors 180 degrees out of phase in adjacent video cycles" - i.e.
    // swapping which pattern goes at even vs. odd addresses produces the
    // complementary color. Independently cross-checked against this
    // codebase's own DrawHiresByte/HiresColorPhaseFollowsColumnParityAndDl7
    // (AppleIISystemVideoModesTests): $2A's lit dots (bits 1,3,5) fall on
    // odd screen columns when $2A sits at an even address (baseX even + odd
    // dot offset = odd column - the green/orange pair), but on *even*
    // columns when $2A sits at an odd address instead (baseX odd + odd dot
    // offset = even column - the violet/blue pair) - so swapping the
    // pattern-to-parity assignment doesn't just "invert" the color in the
    // abstract, it moves the lit dots to the other pair of screen columns
    // entirely, landing on violet (DL7 clear selects violet over blue,
    // exactly as it selects green over orange in the unswapped case).
    private const byte VioletPatternForEvenAddress = GreenPatternForOddAddress; // $55
    private const byte VioletPatternForOddAddress = GreenPatternForEvenAddress; // $2A

    private static void FillHiresByAddressParity(AppleIISystem system, byte evenAddressByte, byte oddAddressByte)
    {
        for (var address = 0x2000; address <= 0x3FFF; address++)
        {
            system.WriteByteDebug((ushort)address, (address % 2 == 0) ? evenAddressByte : oddAddressByte);
        }
    }

    private static void SetHiresPage1(AppleIISystem system)
    {
        system.WriteByteDebug(0xC050, 0); // GRAPHICS
        system.WriteByteDebug(0xC057, 0); // HIRES
        system.WriteByteDebug(0xC054, 0); // PAGE1
    }

    // Rather than guess fixed-fraction sample coordinates (Apple II's real
    // HBL/VBL geometry isn't the generic RS-170A window NtscTiming assumes
    // for IsActiveVideo, and doesn't occupy the entire detected raster -
    // Television.SampleBuffer includes the full detected frame, vertical
    // blanking and all, most of which stays black), this scans every sample
    // and checks only the ones the picture actually lit: since the whole
    // screen was filled with one uniform address-parity pattern, every lit
    // pixel should show close to the same hue - a wrongly-colored lit pixel
    // is a genuine decode failure, not sampling noise. Also confirms a
    // comfortably large number of pixels actually lit up at all, so the
    // check can't vacuously pass against an all-black (broken) picture.
    //
    // A small minority of mismatches is expected, not a bug to chase to
    // zero: Sather (same page as the worked example above) documents a
    // real-hardware edge effect where "an orange dot on the far right of
    // the screen will be cutoff by HBL to make it dark brown" - i.e. the
    // picture's own right-edge column genuinely isn't the pattern's true
    // color on real hardware either, and (confirmed empirically while
    // writing this test) is exactly where this decoder's mismatches
    // cluster. 5% is a generous ceiling comfortably above the ~2% actually
    // observed, while still catching a real regression (e.g. a reversed
    // hue) outright, which would push this far higher.
    private static async Task AssertUniformLitHue(SampleBuffer buffer, Func<RgbaByte, bool> matchesExpectedHue)
    {
        var litCount = 0;
        var mismatchCount = 0;

        foreach (var sample in buffer.Data)
        {
            // Restrict to samples the pipeline itself classified as active
            // video (see Television.ClassifyCurrentSample) - sync/blanking/
            // color-burst samples are excluded outright, rather than relied
            // on to just happen to be dim.
            if (sample.Region != RasterRegion.ActiveVideo)
            {
                continue;
            }

            var pixel = sample.Color;

            // Within active video, the picture's own unlit background is
            // still close to black - only the specific dots the fill
            // pattern set are meant to show real hue.
            if (pixel.R + pixel.G + pixel.B < 60)
            {
                continue;
            }

            litCount++;
            if (!matchesExpectedHue(pixel))
            {
                mismatchCount++;
            }
        }

        // The observed lit region for this fill pattern spans roughly 560
        // of 912 columns across roughly 192 of ~261 rows (the real HIRES
        // picture within the detected full frame) - tens of thousands of
        // pixels; 10,000 is a comfortable, non-flaky floor confirming real
        // content rendered, well short of accidentally requiring exact
        // geometry.
        await Assert.That(litCount).IsGreaterThan(10_000);
        await Assert.That(mismatchCount).IsLessThan((int)(litCount * 0.05));
    }

    // Fills the whole of text page 1 with one screen code, bypassing the
    // scrambled line-base layout - every byte the scanner fetches for the
    // text area reads back the same glyph, so the picture is a uniform field
    // of that character. 0xC8 is a normal (non-inverse) 'H': two full-height
    // vertical strokes per cell, i.e. lots of single-dot-wide verticals, the
    // pattern that fringes hardest under composite decoding.
    private static void FillTextPage1(AppleIISystem system, byte screenCode)
    {
        for (var address = 0x400; address <= 0x7FF; address++)
        {
            system.WriteByteDebug((ushort)address, screenCode);
        }
    }

    // Scans every active-video sample and counts how many lit ones carry a
    // real hue (channel spread well above the grayscale noise floor).
    private static (int Lit, int Colored) CountLitAndColored(SampleBuffer buffer)
    {
        var lit = 0;
        var colored = 0;

        foreach (var sample in buffer.Data)
        {
            if (sample.Region != RasterRegion.ActiveVideo)
            {
                continue;
            }

            var pixel = sample.Color;
            if (pixel.R + pixel.G + pixel.B < 60)
            {
                continue;
            }

            lit++;

            var max = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B));
            var min = Math.Min(pixel.R, Math.Min(pixel.G, pixel.B));
            if (max - min > 40)
            {
                colored++;
            }
        }

        return (lit, colored);
    }

    [Test]
    public async Task Revision1PlusShowsMonochromeTextWithColorKillerEngaged()
    {
        // A Revision 1-or-later board (the default): the color-killer circuit
        // suppresses color burst in full-screen text mode, so Television's
        // PLL never locks and its own color killer decodes the whole picture
        // as grayscale - crisp monochrome text, matching real hardware.
        var system = new AppleIISystem(new AppleIISystemOptions(AppleIIRevision.Revision1Plus));
        system.LoadProgram("");

        BootToIdle(system); // The Autostart ROM leaves the machine in TEXT mode.
        FillTextPage1(system, 0xC8);
        TickOneFrame(system);

        await Assert.That(system.Television.ColorBurstLocked).IsFalse();

        var (lit, colored) = CountLitAndColored(system.Television.SampleBuffer);
        await Assert.That(lit).IsGreaterThan(10_000);
        // Essentially nothing carries hue - a handful of edge samples during
        // the PLL's brief unlock transient are the ceiling, not thousands.
        await Assert.That(colored).IsLessThan(lit / 100);
    }

    [Test]
    public async Task Revision0ShowsArtifactColorOnTextWithNoColorKiller()
    {
        // A Revision 0 board has no color-killer circuit, so burst goes out
        // during text mode too and the same 'H' field fringes green/violet
        // on a color receiver - authentic to that revision.
        var system = new AppleIISystem(new AppleIISystemOptions(AppleIIRevision.Revision0));
        system.LoadProgram("");

        BootToIdle(system);
        FillTextPage1(system, 0xC8);
        TickOneFrame(system);

        await Assert.That(system.Television.ColorBurstLocked).IsTrue();

        var (lit, colored) = CountLitAndColored(system.Television.SampleBuffer);
        await Assert.That(lit).IsGreaterThan(10_000);
        // A large fraction of the lit text carries real hue.
        await Assert.That(colored).IsGreaterThan(lit / 4);
    }

    [Test]
    public async Task HiresGreenLinePatternDecodesToGreen()
    {
        var system = new AppleIISystem();
        system.LoadProgram("");

        BootToIdle(system);
        SetHiresPage1(system);
        FillHiresByAddressParity(system, GreenPatternForEvenAddress, GreenPatternForOddAddress);
        TickOneFrame(system);

        await AssertUniformLitHue(
            system.Television.SampleBuffer,
            pixel => pixel.G > pixel.R + 50 && pixel.G > pixel.B + 50);
    }

    [Test]
    public async Task HiresVioletLinePatternDecodesToViolet()
    {
        var system = new AppleIISystem();
        system.LoadProgram("");

        BootToIdle(system);
        SetHiresPage1(system);
        FillHiresByAddressParity(system, VioletPatternForEvenAddress, VioletPatternForOddAddress);
        TickOneFrame(system);

        await AssertUniformLitHue(
            system.Television.SampleBuffer,
            pixel => pixel.R > pixel.G + 50 && pixel.B > pixel.G + 50);
    }
}
