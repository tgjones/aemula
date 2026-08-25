using System.Collections.Generic;
using System.Threading.Tasks;
using Aemula.Emulation.Systems.SpaceInvaders;

namespace Aemula.Tests.Emulation.Systems.SpaceInvaders;

// Phase 2 of docs/space-invaders-television-plan.md: the H/V sync chain and
// the interrupt trigger it drives, cross-checked against the documented
// timing constants (320 pixel-clocks/line, 262 lines/frame, RST 1 at V=0x80,
// RST 2 at V=0xDA) rather than the deleted 317/10161/83200 approximation.
public class SpaceInvadersSystemVideoTimingTests
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

    [Test]
    public async Task HorizontalCounterCountsThroughVisibleThenBlankingRegion()
    {
        // Starting cold at H=0 (this system's power-on state), one full line
        // is 320 pixel-clock states: 0->255 visible (255 more states after
        // the starting one), then a reload to 192 and 192->255 again (64
        // states) during HBLANK, then a reload back to 0 for the next line.
        var system = new SpaceInvadersSystem();
        system.LoadProgram("");

        var hStates = new List<byte>();
        var hblankStates = new List<bool>();

        for (var i = 0; i < 320; i++)
        {
            TickPixelClock(system);

            var (h, _) = system.GetVideoScannerStateForTests();
            hStates.Add(h);
            hblankStates.Add(system.Hblank);
        }

        var expectedH = new List<byte>();
        for (var h = 1; h <= 255; h++)
        {
            expectedH.Add((byte)h);
        }
        for (var h = 192; h <= 255; h++)
        {
            expectedH.Add((byte)h);
        }
        expectedH.Add(0);

        await Assert.That(hStates).IsEquivalentTo(expectedH);

        // HBLANK is asserted for exactly the reload-192-through-255 span
        // (indices 255-318 in the 0-indexed hStates/hblankStates lists,
        // i.e. the 64 states from the first 192 through the second 255) and
        // low everywhere else.
        for (var i = 0; i < hblankStates.Count; i++)
        {
            var expected = i is >= 255 and < 319;
            await Assert.That(hblankStates[i]).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task FullFrameIsThreeHundredAndTwentyByTwoHundredAndSixtyTwoPixelClocks()
    {
        // One full frame is 320 * 262 = 83840 pixel-clock states. Starting
        // cold at H=0/V=0, the very first pass through V's own terminal
        // count (0xFF) doesn't happen until 255 lines in (V free-runs
        // 0->255 before its reload logic has ever fired), so this collects
        // two full trips around H=0/V=0x20 (the steady-state start-of-frame
        // state) to measure one genuine steady-state frame period between
        // them, rather than trusting the first, still-settling pass.
        var system = new SpaceInvadersSystem();
        system.LoadProgram("");

        var visits = new List<int>();
        var pixelClock = 0;

        for (var i = 0; i < 3 * 320 * 262 && visits.Count < 3; i++)
        {
            TickPixelClock(system);
            pixelClock++;

            var (h, v) = system.GetVideoScannerStateForTests();
            if (h == 0 && v == 0x20)
            {
                visits.Add(pixelClock);
            }
        }

        await Assert.That(visits.Count).IsEqualTo(3);

        var firstSteadyStatePeriod = visits[1] - visits[0];
        var secondSteadyStatePeriod = visits[2] - visits[1];

        await Assert.That(firstSteadyStatePeriod).IsEqualTo(320 * 262);
        await Assert.That(secondSteadyStatePeriod).IsEqualTo(320 * 262);
    }

    [Test]
    public async Task Rst1FiresAtMidScreenWithVBlankLow()
    {
        // Driven off _nextInterrupt (via GetNextInterruptForTests) rather
        // than Cpu.Int, since Int only reflects whether the CPU has
        // acknowledged the interrupt yet (gated on its own INTE flag) -
        // this test is only about the video-timing chain that requests it.
        var system = new SpaceInvadersSystem();
        system.LoadProgram("");

        // V=0x80 is reached 128 lines into the very first (cold, unsettled)
        // pass - no reload has happened yet at this point, so this doesn't
        // need to wait for V's steady-state cycle to kick in.
        for (var i = 0; i < 128 * 320 && system.GetNextInterruptForTests() != 0xCF; i++)
        {
            TickPixelClock(system);
        }

        var (_, v) = system.GetVideoScannerStateForTests();

        await Assert.That(system.GetNextInterruptForTests()).IsEqualTo((byte)0xCF);
        await Assert.That(v).IsEqualTo((byte)0x80);
        await Assert.That(system.Vblank).IsFalse();

        // Sanity-checks the CPU-pin wiring itself, not just the latched
        // vector - Int is set true in the very same tick _nextInterrupt is
        // latched, so it must still read true immediately afterwards.
        await Assert.That(system.Cpu.Int).IsTrue();
    }

    [Test]
    public async Task Rst2FiresAtVBlankStart()
    {
        var system = new SpaceInvadersSystem();
        system.LoadProgram("");

        // V's own terminal count (0xFF), and so the first reload to 0xDA,
        // is reached 255 lines into the cold start. RST 1 (0xCF) fires
        // first, at V=0x80 (see above) - this keeps going past it to RST 2.
        for (var i = 0; i < 260 * 320 && system.GetNextInterruptForTests() != 0xD7; i++)
        {
            TickPixelClock(system);
        }

        var (_, v) = system.GetVideoScannerStateForTests();

        await Assert.That(system.GetNextInterruptForTests()).IsEqualTo((byte)0xD7);
        await Assert.That(v).IsEqualTo((byte)0xDA);
        await Assert.That(system.Vblank).IsTrue();
    }
}
