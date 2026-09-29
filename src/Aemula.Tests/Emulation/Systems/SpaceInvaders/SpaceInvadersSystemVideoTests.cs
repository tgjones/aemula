using System.Threading.Tasks;
using Aemula.Emulation.Systems.SpaceInvaders;

namespace Aemula.Tests.Emulation.Systems.SpaceInvaders;

// The 74166 video shift register, driven per-pixel-clock rather than
// blitted in bulk at VBLANK.
public class SpaceInvadersSystemVideoTests
{
    private static void TickPixelClock(SpaceInvadersSystem system)
    {
        // The pixel clock is master/4 - see SpaceInvadersSystem.CyclesPerSecond
        // and TickVideoTiming's "_masterClock % 4" gate. Uses TickVideoForTests
        // (skips the CPU) rather than Tick() - this test cares about the
        // scan/display path only, and needs RAM to stay frozen exactly as
        // poked below without a running program racing it.
        system.TickVideoForTests();
        system.TickVideoForTests();
        system.TickVideoForTests();
        system.TickVideoForTests();
    }

    /// <summary>
    /// Ticks until H=0/V=0x20 is reached for the <paramref name="count"/>th
    /// time - see SpaceInvadersSystemVideoTimingTests' own frame-period test
    /// for why the counters need to settle out of their cold-start state
    /// before H=0/V=0x20 recurs on a clean 320*262 period.
    /// </summary>
    private static void TickToStartOfLine(SpaceInvadersSystem system, int count)
    {
        var visits = 0;

        while (visits < count)
        {
            TickPixelClock(system);

            var (h, v) = system.GetVideoScannerStateForTests();
            if (h == 0 && v == 0x20)
            {
                visits++;
            }
        }
    }

    [Test]
    public async Task ShiftRegisterOutputMatchesFrozenVramPerPixelClock()
    {
        // No CPU involved at all here (see TickPixelClock) - so this poked
        // VRAM pattern stays frozen for the whole test.
        var system = new SpaceInvadersSystem();

        for (var address = 0x2400; address <= 0x3FFF; address++)
        {
            system.PokeRamForTests((ushort)address, (byte)(address * 37));
        }

        // Past cold-start settling, then one full frame from a known-good
        // alignment.
        TickToStartOfLine(system, 2);

        var checkedPixels = 0;

        for (var i = 0; i < 320 * 262; i++)
        {
            TickPixelClock(system);

            var (h, v) = system.GetVideoScannerStateForTests();

            if (system.Hblank || system.Vblank || v < 0x20)
            {
                continue;
            }

            var address = 0x2000 | (v << 5) | (h >> 3);
            var videoRamValue = (byte)(address * 37);
            var expected = (videoRamValue & (1 << (h & 7))) != 0;

            await Assert.That(system.GetShiftRegisterQhForTests()).IsEqualTo(expected).Because($"v={v:X2} h={h:X2}");
            checkedPixels++;
        }

        await Assert.That(checkedPixels).IsGreaterThan(200 * 256);
    }
}
