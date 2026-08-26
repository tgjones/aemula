using System.Threading.Tasks;
using Aemula.Emulation.Systems.SpaceInvaders;

namespace Aemula.Tests.Emulation.Systems.SpaceInvaders;

// CPU/video RAM bus arbitration - the scan-address formula the 74157 muxes
// feed from, the cadence at which the scanner claims the bus, and the real
// wait states (via Intel8080Chip's READY pin - see Intel8080ChipWaitStateTests
// for the chip-level mechanism) that fall out of the two contending.
public class SpaceInvadersSystemRamArbitrationTests
{
    private static void TickPixelClock(SpaceInvadersSystem system)
    {
        // The pixel clock is master/4 - see SpaceInvadersSystem.CyclesPerSecond
        // and TickVideoTiming's "_masterClock % 4" gate.
        system.Tick();
        system.Tick();
        system.Tick();
        system.Tick();
    }

    /// <summary>
    /// Ticks until H=0/V=0x20 is reached for the <paramref name="count"/>th
    /// time - the same cold-start settling reasoning
    /// SpaceInvadersSystemVideoTimingTests' own frame-period test uses,
    /// since H=0/V=0x20 isn't reached on a clean 320*262 period until the
    /// counters have settled out of their cold-start state.
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
    public async Task ScanAddressFormulaLandsFirstScannedByteAt0x2400()
    {
        var system = new SpaceInvadersSystem();
        system.LoadProgram("");

        // First visible pixel of the first scanned line - V < 0x20 is never
        // scanned (it's free work RAM, not VRAM - see
        // SpaceInvadersSystem.Video.cs's ComputeScanAddress remarks).
        TickToStartOfLine(system, 2);

        await Assert.That(system.GetScanAddressForTests()).IsEqualTo((ushort)0x2400);

        // One byte-time (8 pixel clocks) later, RAB has advanced by
        // exactly one byte.
        for (var i = 0; i < 8; i++)
        {
            TickPixelClock(system);
        }

        await Assert.That(system.GetScanAddressForTests()).IsEqualTo((ushort)0x2401);
    }

    [Test]
    public async Task ScannerClaimsTheBusExactlyThirtyTwoTimesPerActiveLineAndNeverDuringHblank()
    {
        var system = new SpaceInvadersSystem();
        system.LoadProgram("");

        TickToStartOfLine(system, 2);

        var claimsDuringActiveVideo = 0;
        var claimsDuringHblank = 0;

        for (var i = 0; i < 320; i++)
        {
            TickPixelClock(system);

            if (!system.VideoWantsRamForTests)
            {
                continue;
            }

            if (system.Hblank)
            {
                claimsDuringHblank++;
            }
            else
            {
                claimsDuringActiveVideo++;
            }
        }

        // 256 visible pixels / 8 pixels-per-byte = 32 byte-fetches; see
        // ComputeScanAddress's RAB formula.
        await Assert.That(claimsDuringActiveVideo).IsEqualTo(32);
        await Assert.That(claimsDuringHblank).IsEqualTo(0);
    }

    [Test]
    public async Task CpuGenuinelyStallsViaReadyWhenItTouchesRamDuringTheScannersWindow()
    {
        // Runs the real ROM rather than staging a specific instruction:
        // this is deliberately an end-to-end check that TickRamArbitration
        // (Video.cs) and Intel8080Chip's READY-sampled Tw mechanism
        // (Intel8080Chip.cs) are actually wired together, not just each
        // correct in isolation.
        var system = new SpaceInvadersSystem();
        system.LoadProgram("");

        var sawContention = false;

        for (var i = 0; i < 4_000_000 && !sawContention; i++)
        {
            system.Tick();

            if (!system.Cpu.Wait)
            {
                continue;
            }

            sawContention = true;

            // TickRamArbitration only ever pulls READY low - and so only
            // ever lands the CPU in a Tw state - while it's addressing RAM;
            // Address itself doesn't change again until the next machine
            // cycle's T1, so it's still valid to check here regardless of
            // how many Tw states have elapsed (VideoWantsRamForTests isn't
            // checked here too: its own pulse is only one pixel-clock wide,
            // narrower than the 2-master-tick gap between READY being
            // sampled and WAIT becoming externally visible, so it can
            // legitimately have already moved on by this exact tick).
            await Assert.That((system.Cpu.Address & 0x2000) != 0).IsTrue();
        }

        await Assert.That(sawContention).IsTrue();
    }
}
