using System;
using System.Linq;
using System.Threading.Tasks;
using Aemula.Emulation.Systems.BbcMicro;

namespace Aemula.Tests.Emulation.Systems.BbcMicro;

// The monochrome composite output: CSYNC# from the 6845's HS and VS, and the
// ULA's red, green and blue summed into a grey level.
public class BbcMicroCompositeVideoTests
{
    private const uint ShortResetPulseTicks = 100;

    // The OS has put its banner on the screen by this many 16MHz ticks.
    private const int BootTicks = 8_000_000;
    private const int MeasuredTicks = 1_280_000;

    [Test]
    public async Task Output_has_sync_black_and_picture_levels()
    {
        var system = new BbcMicroSystem(null, ShortResetPulseTicks);

        for (var tick = 0; tick < BootTicks; tick++)
        {
            system.Tick();
        }

        var sawSync = false;
        var sawBlack = false;
        var sawPicture = false;

        for (var tick = 0; tick < MeasuredTicks; tick++)
        {
            system.Tick();

            var sample = system.CompositeSampleForTest;
            sawSync |= sample == 0;
            sawBlack |= sample == 77;
            sawPicture |= sample > 77;
        }

        await Assert.That(sawSync).IsTrue();
        await Assert.That(sawBlack).IsTrue();
        await Assert.That(sawPicture).IsTrue();
    }

    [Test]
    public async Task Sync_is_low_only_during_hsync_or_vsync()
    {
        var system = new BbcMicroSystem(null, ShortResetPulseTicks);

        for (var tick = 0; tick < BootTicks; tick++)
        {
            system.Tick();
        }

        var mismatches = 0;

        for (var tick = 0; tick < MeasuredTicks; tick++)
        {
            system.Tick();

            var inSync = system.Crtc.HSync || system.Crtc.VSync;

            if (inSync != (system.CompositeSampleForTest == 0))
            {
                mismatches++;
            }
        }

        await Assert.That(mismatches).IsEqualTo(0);
    }

    [Test]
    public async Task Television_locks_to_a_pal_frame()
    {
        var system = new BbcMicroSystem(null, ShortResetPulseTicks);

        for (var tick = 0; tick < BootTicks + 3 * MeasuredTicks; tick++)
        {
            system.Tick();
        }

        await Assert.That(system.Television.DetectedSamplesPerLine).IsEqualTo(1024f).Within(8f);
        await Assert.That(system.Television.DetectedLinesPerFrame).IsEqualTo(312.5f).Within(1f);
    }

    // The BASIC banner is three lines of Mode 7 text: "BBC Computer 32K" is 16 cells of 16
    // samples (jsbeeb's frame has the same 256 pixel width), and nothing is lit outside the 40
    // cells of the displayed area.
    [Test]
    public async Task Banner_is_drawn_inside_the_forty_character_window()
    {
        var system = new BbcMicroSystem(null, ShortResetPulseTicks);

        for (var tick = 0; tick < BootTicks + 2 * MeasuredTicks; tick++)
        {
            system.Tick();
        }

        var television = system.Television;
        var buffer = television.SampleBuffer;
        var width = (int)buffer.Width;
        var (startRow, rowCount) = television.ComputeActiveVideoRowRange();
        var litCountByRow = new int[rowCount];
        var minColumn = int.MaxValue;
        var maxColumn = int.MinValue;

        for (var row = 0; row < rowCount; row++)
        {
            for (var column = 0; column < width; column++)
            {
                if (buffer.Data[(startRow + row) * width + column].Color.R > 128)
                {
                    litCountByRow[row]++;
                    minColumn = Math.Min(minColumn, column);
                    maxColumn = Math.Max(maxColumn, column);
                }
            }
        }

        // The first text row is the first row with any lit sample; the banner's widest line is
        // "BBC Computer 32K", the prompt's the cursor.
        var litRows = litCountByRow.Count(count => count > 0);

        await Assert.That(litRows).IsGreaterThan(0);
        await Assert.That(maxColumn - minColumn).IsLessThan(40 * 16);
        await Assert.That(maxColumn - minColumn).IsGreaterThan(14 * 16);
    }

    // Both fields are 312.5 lines of 64us: 320000 ticks of the 16MHz clock between one frame
    // wrap and the next, to within a line.
    [Test]
    public async Task Field_period_is_fifty_hertz()
    {
        var system = new BbcMicroSystem(null, ShortResetPulseTicks);

        for (var tick = 0; tick < BootTicks + 3 * MeasuredTicks; tick++)
        {
            system.Tick();
        }

        var previousRow = system.Television.CurrentRow;
        var lastWrap = -1;
        var periods = new System.Collections.Generic.List<int>();

        for (var tick = 0; tick < 4 * 320_000 && periods.Count < 3; tick++)
        {
            system.Tick();

            var row = system.Television.CurrentRow;

            if (row < previousRow)
            {
                if (lastWrap >= 0)
                {
                    periods.Add(tick - lastWrap);
                }

                lastWrap = tick;
            }

            previousRow = row;
        }

        await Assert.That(periods.Count).IsEqualTo(3);

        foreach (var period in periods)
        {
            await Assert.That(period).IsEqualTo(320_000).Within(1024);
        }
    }
}
