using System.Threading.Tasks;
using Aemula.Emulation.Output;
using Aemula.Emulation.Systems.SpaceInvaders;

namespace Aemula.Tests.Emulation.Systems.SpaceInvaders;

// The full composite-video pipeline (the blanking gate, the analog summing
// stage, and the fractional resampler feeding Television), verified
// end-to-end through Television itself - the same "SystemTelevisionTests"
// shape Atari2600SystemTelevisionTests and AppleIISystemTelevisionTests use:
// no color burst (this board has none), but Television locks onto a genuine
// ~15.6kHz/60Hz raster and frames the picture correctly. RAM-arbitration
// wait-state behavior and interrupt-trigger correctness have their own
// targeted tests in SpaceInvadersSystemRamArbitrationTests and
// SpaceInvadersSystemVideoTimingTests respectively.
public class SpaceInvadersSystemTelevisionTests
{
    // Master ticks in one full frame: 320 pixel-clock states/line * 262
    // lines/frame * 4 master ticks/pixel-clock (see
    // SpaceInvadersSystem.CyclesPerSecond and TickVideoTiming's own
    // "_masterClock % 4" gate).
    private const int TicksPerFrame = 320 * 262 * 4;

    // Poking VRAM directly (bypassing the CPU/ROM, same as
    // SpaceInvadersSystemVideoTests) and driving TickVideoForTests (skips
    // the CPU entirely) - this test is about the video-scan-to-Television
    // path, not the game program, so there's no need for a running CPU or
    // real game code.
    private static SpaceInvadersSystem BuildSystemWithHalfWhiteHalfBlackPicture()
    {
        var system = new SpaceInvadersSystem();

        // Top half of the visible picture (V=0x20-0x8F): every VRAM byte
        // 0xFF -> eight consecutive white columns per byte (Qh serializes
        // LSB-first, so an all-1s byte is simply eight white pixels
        // regardless of bit order).
        for (var v = 0x20; v < 0x90; v++)
        {
            for (var x = 0; x < 32; x++)
            {
                system.PokeRamForTests((ushort)(0x2000 | (v << 5) | x), 0xFF);
            }
        }

        // Bottom half (V=0x90-0xFF): all-black columns.
        for (var v = 0x90; v <= 0xFF; v++)
        {
            for (var x = 0; x < 32; x++)
            {
                system.PokeRamForTests((ushort)(0x2000 | (v << 5) | x), 0x00);
            }
        }

        return system;
    }

    // Enough frames for Television's self-calibrating sync separator, raster
    // oscillators, and vertical-blanking detector to lock - same "just run
    // it for a while" approach Atari2600SystemTelevisionTests.RunFrames uses,
    // scaled down from that test's 20 frames since this board's picture
    // geometry is perfectly regular (a free-running digital counter chain,
    // no analog jitter to average out).
    private static void RunFrames(SpaceInvadersSystem system, int frameCount)
    {
        for (var i = 0; i < frameCount * TicksPerFrame; i++)
        {
            system.TickVideoForTests();
        }
    }

    [Test]
    public async Task PictureLocksIntoAStableRasterAndFramesActiveVideoCorrectly()
    {
        var system = BuildSystemWithHalfWhiteHalfBlackPicture();

        RunFrames(system, 10);

        // No color burst on this board at all (it's a monochrome composite
        // signal - the cabinet's color is a physical cellophane overlay,
        // no video circuitry involved) - ColorBurstLocked correctly reads
        // false, same as it would for a real monochrome monitor never
        // seeing a burst.
        await Assert.That(system.Television.ColorBurstLocked).IsFalse();

        // The real raster shape here isn't 320x262 in Television's own
        // sample domain (that's this system's own pixel-clock/line-count
        // domain - see SpaceInvadersSystem.CompositeVideo.cs's remarks on
        // why the two clocks don't share a fixed ratio): one line lasts
        // 320 pixel clocks at 4.992MHz = ~64.10us, which at Television's
        // assumed 4*fsc (14.318180MHz) sample rate is ~917.8 samples/line -
        // this board's real ~15.6kHz horizontal rate being genuinely
        // slightly slower than broadcast NTSC's 15.734kHz. Bounds below
        // give the self-calibrating estimate comfortable room, the same
        // style NtscRasterOscillatorsTests uses for its own locked-signal
        // assertions.
        await Assert.That(system.Television.DetectedSamplesPerLine).IsBetween(900.0f, 935.0f);
        await Assert.That(system.Television.DetectedLinesPerFrame).IsBetween(258.0f, 266.0f);

        var buffer = system.Television.SampleBuffer;

        // Averages each row's luma (only rows that are mostly active video -
        // the same 50%-of-width threshold Atari2600SystemTelevisionTests'
        // FindDistinctRowBands uses to tell real picture rows from
        // blanking), then classifies each row as "bright" or "dark" against
        // the midpoint between BlankingLevel and WhiteLevel. This doesn't
        // assume which buffer row corresponds to V=0x20 (Television's row 0
        // is wherever its own vsync-relative timing puts it, not
        // necessarily this system's own V=0x20) - only that the poked
        // half-white/half-black VRAM pattern shows up as two large,
        // correctly-leveled bands somewhere in the buffer.
        var activeVideoCount = 0;
        var brightRowCount = 0;
        var darkRowCount = 0;
        double brightRowLumaSum = 0;
        double darkRowLumaSum = 0;

        for (var row = 0; row < buffer.Height; row++)
        {
            double rowLumaSum = 0;
            var rowSampleCount = 0;

            var rowOffset = row * buffer.Width;
            for (var column = 0; column < buffer.Width; column++)
            {
                var sample = buffer.Data[rowOffset + column];
                if (sample.Region != RasterRegion.ActiveVideo)
                {
                    continue;
                }

                activeVideoCount++;
                rowLumaSum += sample.Luma;
                rowSampleCount++;
            }

            if (rowSampleCount < buffer.Width * 0.5f)
            {
                continue;
            }

            var rowAverageLuma = rowLumaSum / rowSampleCount;

            if (rowAverageLuma > 160.0)
            {
                brightRowCount++;
                brightRowLumaSum += rowAverageLuma;
            }
            else
            {
                darkRowCount++;
                darkRowLumaSum += rowAverageLuma;
            }
        }

        // A real, substantial fraction of the buffer decoded as active
        // video - not just a handful of stray samples - confirming
        // IsActiveVideo genuinely frames the picture rather than
        // misclassifying most of it as blanking.
        await Assert.That(activeVideoCount).IsGreaterThan((int)(buffer.Width * buffer.Height / 4));

        // Both halves of the poked pattern show up as real, substantially-
        // sized bands (not just a few stray rows near a misclassified
        // boundary), each at its correctly-decoded level.
        await Assert.That(brightRowCount).IsGreaterThan(50);
        await Assert.That(darkRowCount).IsGreaterThan(50);
        await Assert.That(brightRowLumaSum / brightRowCount).IsGreaterThan(200.0);
        await Assert.That(darkRowLumaSum / darkRowCount).IsLessThan(100.0);
    }
}
