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
}
