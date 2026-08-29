using System.Threading.Tasks;
using Aemula.Emulation.Systems.AppleII;

namespace Aemula.Tests.Emulation.Systems.AppleII;

// The $C030 speaker toggle, the $C058-$C05F annunciators, and the
// $C061-$C067 game-connector reads (three pushbuttons, four paddle
// one-shots retriggered by $C070), cross-checked against Jim Sather's
// "Understanding the Apple II" chapter 7.
public class AppleIISystemGameIoTests
{
    private static AppleIISystem BootToIdle()
    {
        var system = new AppleIISystem();
        system.LoadProgram("");

        // Enough to get the Autostart ROM into its keyboard-wait loop, which
        // touches none of the game-I/O soft switches - matching the budget
        // the other AppleII system tests use.
        for (var i = 0; i < 500_000; i++)
        {
            system.Tick();
        }

        return system;
    }

    [Test]
    public async Task SpeakerFlipFlopTogglesOnEveryC030Access()
    {
        var system = BootToIdle();

        var initial = system.SpeakerBit;

        // No ticks between the accesses, so only these strobes move it.
        system.ReadByteDebug(0xC030);
        await Assert.That(system.SpeakerBit).IsEqualTo(!initial);

        system.ReadByteDebug(0xC03F);
        await Assert.That(system.SpeakerBit).IsEqualTo(initial);

        // A write to the range clicks it just like a read.
        system.WriteByteDebug(0xC030, 0);
        await Assert.That(system.SpeakerBit).IsEqualTo(!initial);
    }

    [Test]
    public async Task AnnunciatorsLatchFromC058ToC05F()
    {
        var system = BootToIdle();

        // Each annunciator has an off address (even) and an on address (odd),
        // latched by the same 74LS259 as the screen-mode switches.
        system.WriteByteDebug(0xC059, 0); // AN0 on
        await Assert.That(system.Annunciator0).IsTrue();

        system.WriteByteDebug(0xC058, 0); // AN0 off
        await Assert.That(system.Annunciator0).IsFalse();

        system.WriteByteDebug(0xC05F, 0); // AN3 on
        await Assert.That(system.Annunciator3).IsTrue();
        await Assert.That(system.Annunciator0).IsFalse(); // untouched

        system.WriteByteDebug(0xC05E, 0); // AN3 off
        await Assert.That(system.Annunciator3).IsFalse();
    }

    [Test]
    public async Task PushbuttonsReadThroughBitSevenOfC061ToC063()
    {
        var system = BootToIdle();

        await Assert.That(system.ReadByteDebug(0xC061) & 0x80).IsEqualTo(0x00);

        system.SetPushButton(0, true);
        await Assert.That(system.ReadByteDebug(0xC061) & 0x80).IsEqualTo(0x80);

        // The mux addressing is per-button: PB0 pressed doesn't show at PB1/PB2.
        await Assert.That(system.ReadByteDebug(0xC062) & 0x80).IsEqualTo(0x00);
        await Assert.That(system.ReadByteDebug(0xC063) & 0x80).IsEqualTo(0x00);

        system.SetPushButton(2, true);
        await Assert.That(system.ReadByteDebug(0xC063) & 0x80).IsEqualTo(0x80);
        await Assert.That(system.ReadByteDebug(0xC062) & 0x80).IsEqualTo(0x00);

        system.SetPushButton(0, false);
        await Assert.That(system.ReadByteDebug(0xC061) & 0x80).IsEqualTo(0x00);
    }

    // Counts how long PADDLn stays high after a $C070 strobe, in master ticks,
    // polling the one-shot output directly (no CPU involvement).
    private static int MeasurePaddleOneShotTicks(AppleIISystem system, int paddle)
    {
        var address = (ushort)(0xC064 + paddle);

        system.ReadByteDebug(0xC070); // strobe: retriggers all four one-shots

        var ticks = 0;
        while ((system.ReadByteDebug(address) & 0x80) != 0 && ticks < 100_000)
        {
            system.Tick();
            ticks++;
        }

        return ticks;
    }

    [Test]
    public async Task PaddleOneShotDurationRisesMonotonicallyWithPosition()
    {
        var system = BootToIdle();

        system.SetPaddlePosition(0, 0);
        var atZero = MeasurePaddleOneShotTicks(system, 0);

        system.SetPaddlePosition(0, 64);
        var atLow = MeasurePaddleOneShotTicks(system, 0);

        system.SetPaddlePosition(0, 192);
        var atHigh = MeasurePaddleOneShotTicks(system, 0);

        await Assert.That(atZero).IsLessThan(atLow);
        await Assert.That(atLow).IsLessThan(atHigh);

        // Position 0 is essentially instant (just the 100-ohm series floor).
        await Assert.That(atZero).IsLessThan(500);

        // Full scale is the classic "a bit under 3 ms" (Sather ch. 7): at
        // 14.31818 MHz that's ~40k ticks, comfortably in the millisecond
        // decade rather than micro- or tens-of-milli-.
        system.SetPaddlePosition(0, 255);
        var atFullScale = MeasurePaddleOneShotTicks(system, 0);
        await Assert.That(atFullScale).IsGreaterThan(30_000);
        await Assert.That(atFullScale).IsLessThan(50_000);
    }

    [Test]
    public async Task EachPaddleTimesIndependently()
    {
        var system = BootToIdle();

        system.SetPaddlePosition(1, 0);
        system.SetPaddlePosition(2, 200);

        system.ReadByteDebug(0xC070); // one strobe retriggers all four

        for (var i = 0; i < 3_000; i++)
        {
            system.Tick();
        }

        // ~3000 ticks in: paddle 1 (position 0) has long since timed out,
        // paddle 2 (position 200, ~31k ticks) is still running.
        await Assert.That(system.ReadByteDebug(0xC065) & 0x80).IsEqualTo(0x00);
        await Assert.That(system.ReadByteDebug(0xC066) & 0x80).IsEqualTo(0x80);
    }

    [Test]
    public async Task RestrobeRetriggersATimedOutPaddle()
    {
        var system = BootToIdle();

        system.SetPaddlePosition(3, 0);
        system.ReadByteDebug(0xC070);

        for (var i = 0; i < 1_000; i++)
        {
            system.Tick();
        }

        await Assert.That(system.ReadByteDebug(0xC067) & 0x80).IsEqualTo(0x00);

        system.SetPaddlePosition(3, 200);
        system.ReadByteDebug(0xC070);

        await Assert.That(system.ReadByteDebug(0xC067) & 0x80).IsEqualTo(0x80);
    }

    [Test]
    public async Task PreadStylePollLoopReadsBackTheSetPosition()
    {
        // The Autostart Monitor's PREAD ($FB1E) strobes $C070 then polls
        // PADDLn in an 11-cycle loop, returning the iteration count (0-255).
        // A 6502 cycle is a nominal 14 master ticks here, so mirroring that
        // loop - 11*14 ticks per iteration - should recover the position
        // that was set, across the whole range. This is the calibration the
        // one-shot's per-count scaling exists to satisfy.
        const int ticksPerPollIteration = 11 * 14;

        int Pread(AppleIISystem system, int paddle)
        {
            var address = (ushort)(0xC064 + paddle);
            system.ReadByteDebug(0xC070);

            var count = 0;
            while (count < 255)
            {
                for (var t = 0; t < ticksPerPollIteration; t++)
                {
                    system.Tick();
                }

                if ((system.ReadByteDebug(address) & 0x80) == 0)
                {
                    break;
                }

                count++;
            }

            return count;
        }

        var system = BootToIdle();

        foreach (var position in new[] { 0, 50, 127, 200 })
        {
            system.SetPaddlePosition(0, (byte)position);
            var reading = Pread(system, 0);
            await Assert.That(reading).IsGreaterThanOrEqualTo(position - 2);
            await Assert.That(reading).IsLessThanOrEqualTo(position + 2);
        }

        system.SetPaddlePosition(0, 255);
        await Assert.That(Pread(system, 0)).IsGreaterThanOrEqualTo(253);
    }
}
