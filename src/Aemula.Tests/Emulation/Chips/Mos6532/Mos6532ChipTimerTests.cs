using System.Threading.Tasks;
using Aemula.Emulation.Chips.Mos6532;

namespace Aemula.Tests.Emulation.Chips.Mos6532;

// Exercises the interval timer through the real pin interface (address decode,
// Phi2 edges) rather than the internal Timer class, so the wiring in
// Mos6532Chip.Phi2 is covered alongside Timer itself. A register access takes a
// whole CPU cycle - Phi2 rising does the access, Phi2 falling ticks the timer -
// so every access here also advances the timer by one clock, exactly as on
// hardware.
public class Mos6532ChipTimerTests
{
    // A0/A1 pick the interval, A2 selects the timer, A4 makes it a write.
    private const byte WriteTim8T = 0x15;
    private const byte WriteTim64T = 0x16;
    private const byte WriteTim1024T = 0x17;

    // A2 selects the timer, A0 clear = read INTIM (rather than the flags).
    private const byte ReadIntim = 0x04;

    private static void WriteTimer(Mos6532Chip riot, byte address, byte value)
    {
        riot.CS1 = true;   // A7 high
        riot.CS2 = false;  // A12 low  -> selected
        riot.RS = true;    // A9 high  -> I/O + timer, not RAM
        riot.RW = false;
        riot.A = address;
        riot.DB = value;
        riot.Phi2 = true;  // register access on the rising edge
        riot.Phi2 = false; // timer ticks on the falling edge
    }

    private static byte ReadIntimRegister(Mos6532Chip riot)
    {
        riot.CS1 = true;
        riot.CS2 = false;
        riot.RS = true;
        riot.RW = true;
        riot.A = ReadIntim;
        riot.Phi2 = true;
        var value = riot.DB;
        riot.Phi2 = false;
        return value;
    }

    // CPU cycles with the chip deselected: nothing is accessed, but the timer
    // still ticks on each Phi2 falling edge.
    private static void IdleCycles(Mos6532Chip riot, int count)
    {
        riot.CS1 = false;
        for (var i = 0; i < count; i++)
        {
            riot.Phi2 = true;
            riot.Phi2 = false;
        }
    }

    [Test]
    public async Task ReadingIntimJustAfterWritingReturnsOneLessThanWritten()
    {
        var riot = new Mos6532Chip();

        // The 6532 "read one cycle after write" quirk: the write cycle's own
        // timer clock has already stepped INTIM down by one by the time any
        // later instruction can read it back.
        WriteTimer(riot, WriteTim64T, 37);

        await Assert.That(ReadIntimRegister(riot)).IsEqualTo((byte)36);
    }

    [Test]
    [Arguments(WriteTim8T, 8)]
    [Arguments(WriteTim64T, 64)]
    [Arguments(WriteTim1024T, 1024)]
    public async Task IntimCountsDownAtTheSelectedIntervalRate(byte writeAddress, int interval)
    {
        var riot = new Mos6532Chip();

        // After the write, INTIM sits at 39 for the first whole interval.
        WriteTimer(riot, writeAddress, 40);

        IdleCycles(riot, interval);
        await Assert.That(ReadIntimRegister(riot)).IsEqualTo((byte)38);

        IdleCycles(riot, interval);
        await Assert.That(ReadIntimRegister(riot)).IsEqualTo((byte)37);
    }

    [Test]
    public async Task TimerFlagAssertsIrqOnlyAfterUnderflow()
    {
        var riot = new Mos6532Chip();

        // A3 (0x08) set alongside the TIM8T write enables the timer interrupt.
        WriteTimer(riot, (byte)(WriteTim8T | 0x08), 4);
        await Assert.That(riot.Irq).IsTrue(); // active low: not yet asserted

        // Underflow is one clock into the interval after INTIM reaches 0.
        IdleCycles(riot, 5 * 8);

        // Irq is refreshed on a selected Phi2 rising edge; a read provides one.
        riot.CS1 = true;
        riot.CS2 = false;
        riot.RS = true;
        riot.RW = true;
        riot.A = 0x0C; // A2 timer, A3 keeps the interrupt enabled, A0 clear = read INTIM
        riot.Phi2 = true;
        await Assert.That(riot.Irq).IsFalse(); // active low: asserted
        riot.Phi2 = false;
    }
}
