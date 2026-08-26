using System.Threading.Tasks;
using Aemula.Emulation.Systems.SpaceInvaders;

namespace Aemula.Tests.Emulation.Systems.SpaceInvaders;

// Phase 4 of docs/space-invaders-television-plan.md: the 74166 video shift
// register, driven per-pixel-clock rather than blitted in bulk at VBLANK.
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
    public async Task PerPixelScanMatchesTheOldBulkBlitFormulaForAFrozenVram()
    {
        // No CPU involved at all here (see TickPixelClock) - so this poked
        // VRAM pattern stays frozen for the whole test, the same way the
        // deleted UpdateDisplay() would have read it in one atomic
        // end-of-frame pass.
        var system = new SpaceInvadersSystem();

        for (var address = 0x2400; address <= 0x3FFF; address++)
        {
            system.PokeRamForTests((ushort)address, (byte)(address * 37));
        }

        // Past cold-start settling, then one full frame from a known-good
        // alignment - guarantees every pixel has been (re)written from the
        // frame's steady-state scan by the time this returns.
        TickToStartOfLine(system, 2);
        for (var i = 0; i < 320 * 262; i++)
        {
            TickPixelClock(system);
        }

        for (var v = 0x20; v <= 0xFF; v++)
        {
            for (var x = 0; x < 32; x++)
            {
                var address = 0x2000 | (v << 5) | x;
                var videoRamValue = (byte)(address * 37);

                byte mask = 1;
                for (var b = 0; b < 8; b++)
                {
                    var expected = (videoRamValue & mask) != 0 ? (byte)0xFF : (byte)0;
                    var outputAddress = v * 256 + x * 8 + b;
                    var actual = system.Display.Data[outputAddress];

                    await Assert.That(actual.R).IsEqualTo(expected).Because($"v={v:X2} h={x * 8 + b:X2}");
                    await Assert.That(actual.A).IsEqualTo((byte)0xFF);

                    mask <<= 1;
                }
            }
        }

        // Rows below V=0x20 are $2000-$23FF work RAM, not VRAM - never
        // scanned (see the plan's "Correcting the existing code" section)
        // and so must stay untouched (DisplayBuffer's own opaque-black
        // construction default - see DisplayBuffer.Resize) even after the
        // cold-start's one-time transient pass through V<0x20.
        for (var address = 0; address < 32 * 256; address++)
        {
            await Assert.That(system.Display.Data[address].R).IsEqualTo((byte)0);
            await Assert.That(system.Display.Data[address].A).IsEqualTo((byte)0xFF);
        }
    }
}
