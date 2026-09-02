using System.Threading.Tasks;
using Aemula.Emulation.Chips.Mos6532;

namespace Aemula.Tests.Emulation.Chips.Mos6532;

// Reference behaviour taken from Stella's M6532 (setTimerRegister writes the
// value straight to INTIM; the first timer clock one cycle later takes it to
// value - 1; from there it steps down once per `interval` cycles; INTIM stays
// at 0 for one more whole interval, then underflows to 0xFF, sets the timer
// flag, and from there decrements once per cycle). "Tick N" below counts timer
// clocks from immediately after the register write.
public class TimerTests
{
    private static void Tick(Timer timer, int count)
    {
        for (var i = 0; i < count; i++)
        {
            timer.Tick();
        }
    }

    [Test]
    public async Task ResetLoadsTheWrittenValue()
    {
        var timer = new Timer();

        timer.Reset(50, 64);

        await Assert.That(timer.Value).IsEqualTo((byte)50);
        await Assert.That(timer.Expired).IsFalse();
    }

    [Test]
    [Arguments((ushort)1)]
    [Arguments((ushort)8)]
    [Arguments((ushort)64)]
    [Arguments((ushort)1024)]
    public async Task FirstTimerClockTakesValueToValueMinusOne(ushort interval)
    {
        var timer = new Timer();
        timer.Reset(50, interval);

        timer.Tick();
        await Assert.That(timer.Value).IsEqualTo((byte)49);

        // Then it holds for the rest of the interval - the next step is the
        // interval'th clock after this one.
        Tick(timer, interval - 1);
        await Assert.That(timer.Value).IsEqualTo((byte)49);

        timer.Tick();
        await Assert.That(timer.Value).IsEqualTo((byte)48);
    }

    [Test]
    [Arguments((ushort)1)]
    [Arguments((ushort)8)]
    [Arguments((ushort)64)]
    [Arguments((ushort)1024)]
    public async Task DecrementsOncePerIntervalDownToZero(ushort interval)
    {
        var timer = new Timer();
        timer.Reset(4, interval);

        // The step happens one clock into each interval, so 1 + n*interval
        // clocks after the write leaves Value at 4 - 1 - n.
        timer.Tick();
        await Assert.That(timer.Value).IsEqualTo((byte)3);

        for (byte expected = 2; expected != 0xFF; expected--)
        {
            Tick(timer, interval);
            await Assert.That(timer.Value).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task ValueStaysAtZeroForOneWholeIntervalBeforeUnderflow()
    {
        var timer = new Timer();
        timer.Reset(2, 64);

        // Value hits 0 one clock into the last counting interval: 1 + 1*64.
        Tick(timer, 1 + 64);
        await Assert.That(timer.Value).IsEqualTo((byte)0);
        await Assert.That(timer.Expired).IsFalse();

        // Still zero, still not expired, right up to the last clock of the
        // grace interval (underflow is at 1 + 2*64).
        Tick(timer, 63);
        await Assert.That(timer.Value).IsEqualTo((byte)0);
        await Assert.That(timer.Expired).IsFalse();

        timer.Tick();
        await Assert.That(timer.Value).IsEqualTo((byte)0xFF);
        await Assert.That(timer.Expired).IsTrue();
    }

    [Test]
    [Arguments((ushort)1, (byte)5)]
    [Arguments((ushort)8, (byte)5)]
    [Arguments((ushort)64, (byte)43)]
    [Arguments((ushort)1024, (byte)2)]
    public async Task UnderflowHappensOneClockIntoTheIntervalAfterValueReachesZero(ushort interval, byte value)
    {
        var timer = new Timer();
        timer.Reset(value, interval);

        // Underflow is 1 + value*interval clocks after the write.
        Tick(timer, value * interval);
        await Assert.That(timer.Expired).IsFalse();

        timer.Tick();
        await Assert.That(timer.Value).IsEqualTo((byte)0xFF);
        await Assert.That(timer.Expired).IsTrue();
    }

    [Test]
    public async Task DecrementsEveryCycleAfterUnderflow()
    {
        var timer = new Timer();
        timer.Reset(1, 64);

        Tick(timer, 1 + 64); // through the count-to-zero interval to underflow
        await Assert.That(timer.Value).IsEqualTo((byte)0xFF);
        await Assert.That(timer.Expired).IsTrue();

        timer.Tick();
        await Assert.That(timer.Value).IsEqualTo((byte)0xFE);

        Tick(timer, 0xFE); // wrap all the way back around
        await Assert.That(timer.Value).IsEqualTo((byte)0x00);
        await Assert.That(timer.Expired).IsTrue();

        timer.Tick();
        await Assert.That(timer.Value).IsEqualTo((byte)0xFF);
        await Assert.That(timer.Expired).IsTrue();
    }

    [Test]
    [Arguments((ushort)1)]
    [Arguments((ushort)64)]
    [Arguments((ushort)1024)]
    public async Task WritingZeroUnderflowsOnTheNextCycle(ushort interval)
    {
        var timer = new Timer();
        timer.Reset(0, interval);

        await Assert.That(timer.Value).IsEqualTo((byte)0);
        await Assert.That(timer.Expired).IsFalse();

        timer.Tick();
        await Assert.That(timer.Value).IsEqualTo((byte)0xFF);
        await Assert.That(timer.Expired).IsTrue();
    }

    [Test]
    public async Task ResetClearsExpiredAndRestoresIntervalRateCounting()
    {
        var timer = new Timer();

        // Drive it well past underflow so it is free-running once per cycle.
        timer.Reset(1, 8);
        Tick(timer, 500);
        await Assert.That(timer.Expired).IsTrue();

        timer.Reset(40, 64);
        await Assert.That(timer.Value).IsEqualTo((byte)40);
        await Assert.That(timer.Expired).IsFalse();

        // Interval-rate again, not the stale once-per-cycle mode: the first
        // clock steps to 39, then it holds for a full 64.
        timer.Tick();
        await Assert.That(timer.Value).IsEqualTo((byte)39);

        Tick(timer, 63);
        await Assert.That(timer.Value).IsEqualTo((byte)39);

        timer.Tick();
        await Assert.That(timer.Value).IsEqualTo((byte)38);
    }

    [Test]
    public async Task ResetMidIntervalRestartsThePrescaler()
    {
        var timer = new Timer();
        timer.Reset(10, 64);
        Tick(timer, 100); // partway into the second interval

        timer.Reset(20, 64);
        await Assert.That(timer.Value).IsEqualTo((byte)20);

        timer.Tick();
        await Assert.That(timer.Value).IsEqualTo((byte)19);

        Tick(timer, 63);
        await Assert.That(timer.Value).IsEqualTo((byte)19);

        timer.Tick();
        await Assert.That(timer.Value).IsEqualTo((byte)18);
    }
}
