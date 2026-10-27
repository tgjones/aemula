using System.Threading.Tasks;
using Aemula.Emulation.Chips.Mos6522;
using static Aemula.Tests.Emulation.Chips.Mos6522.Mos6522Bus;

namespace Aemula.Tests.Emulation.Chips.Mos6522;

// Exercises the VIA through its bus pins, following the behaviour described
// in the datasheet's register and timing sections.
public class Mos6522ChipTests
{
    [Test]
    public async Task ResetClearsRegistersAndLeavesIrqInactive()
    {
        var via = Reset();
        Write(via, Ddra, 0xFF);
        Write(via, Ier, 0xC0);
        Write(via, Acr, 0x40);

        via.Res = false;
        via.Res = true;

        await Assert.That(Read(via, Ddra)).IsEqualTo((byte)0);
        await Assert.That(Read(via, Ier)).IsEqualTo((byte)0x80);
        await Assert.That(Read(via, Acr)).IsEqualTo((byte)0);
        await Assert.That(via.Irq).IsTrue();
    }

    [Test]
    public async Task PortsDriveOutputBitsAndPassInputBits()
    {
        var via = Reset();
        via.PA = 0b0000_1111;
        Write(via, Ddra, 0b1111_0000);
        Write(via, Ora, 0b1010_0000);

        await Assert.That(via.PortA).IsEqualTo((byte)0b1010_1111);
        await Assert.That(via.PaDirection).IsEqualTo((byte)0b1111_0000);
        await Assert.That(Read(via, Ora)).IsEqualTo((byte)0b1010_1111);
    }

    [Test]
    public async Task ReadingPortBReturnsOutputRegisterForOutputBitsAndPinsForInputBits()
    {
        var via = Reset();
        via.PB = 0b1100_1100;
        Write(via, Ddrb, 0b0000_1111);
        Write(via, Orb, 0b0000_1010);

        await Assert.That(Read(via, Orb)).IsEqualTo((byte)0b1100_1010);
    }

    [Test]
    public async Task ChipIsIgnoredWhenNotSelected()
    {
        var via = Reset();
        Write(via, Ddra, 0x55);

        via.Cs2 = true;
        via.RS = Ddra;
        via.RW = false;
        via.DB = 0xAA;
        via.Phi2 = true;
        via.Phi2 = false;

        await Assert.That(via.PaDirection).IsEqualTo((byte)0x55);
    }

    [Test]
    public async Task IerSetAndClearModesAndIfrBit7Summary()
    {
        var via = Reset();

        Write(via, Ier, 0b1100_0010);
        await Assert.That(Read(via, Ier)).IsEqualTo((byte)0b1100_0010);

        Write(via, Ier, 0b0100_0000);
        await Assert.That(Read(via, Ier)).IsEqualTo((byte)0b1000_0010);

        // CA1 flag set, enabled -> IFR bit 7 and IRQ low.
        via.Ca1 = true;
        via.Ca1 = false;
        await Assert.That(Read(via, Ifr)).IsEqualTo((byte)0b1000_0010);
        await Assert.That(via.Irq).IsFalse();

        Write(via, Ifr, 0b0000_0010);
        await Assert.That(Read(via, Ifr)).IsEqualTo((byte)0);
        await Assert.That(via.Irq).IsTrue();
    }

    [Test]
    public async Task DisabledFlagSetsIfrBitButNotIrq()
    {
        var via = Reset();

        via.Ca1 = true;
        via.Ca1 = false;

        await Assert.That(Read(via, Ifr)).IsEqualTo((byte)0x02);
        await Assert.That(via.Irq).IsTrue();
    }

    [Test]
    public async Task Ca1TriggersOnNegativeEdgeByDefaultAndPositiveWhenPcrBit0Set()
    {
        var via = Reset();
        via.Ca1 = true;
        await Assert.That(Read(via, Ifr)).IsEqualTo((byte)0);
        via.Ca1 = false;
        await Assert.That(Read(via, Ifr)).IsEqualTo((byte)0x02);

        Write(via, Ifr, 0x02);
        Write(via, Pcr, 0x01);
        via.Ca1 = true;
        await Assert.That(Read(via, Ifr)).IsEqualTo((byte)0x02);
    }

    [Test]
    public async Task ReadingOraClearsCa1AndCa2FlagsButNotInIndependentMode()
    {
        var via = Reset();
        via.Ca1 = true;
        via.Ca1 = false;
        via.Ca2 = true;
        via.Ca2 = false;
        await Assert.That(Read(via, Ifr)).IsEqualTo((byte)0x03);

        Read(via, Ora);
        await Assert.That(Read(via, Ifr)).IsEqualTo((byte)0);

        // Independent negative edge: reading ORA leaves the CA2 flag.
        Write(via, Pcr, 0b0010);
        via.Ca1 = true;
        via.Ca1 = false;
        via.Ca2 = true;
        via.Ca2 = false;
        Read(via, Ora);
        await Assert.That(Read(via, Ifr)).IsEqualTo((byte)0x01);
    }

    [Test]
    public async Task ReadingOraWithoutHandshakeLeavesFlags()
    {
        var via = Reset();
        via.Ca1 = true;
        via.Ca1 = false;

        Read(via, OraNoHandshake);

        await Assert.That(Read(via, Ifr)).IsEqualTo((byte)0x02);
    }

    [Test]
    public async Task Ca2HandshakeGoesLowAfterOraReadAndHighOnCa1Edge()
    {
        var via = Reset();
        Write(via, Pcr, 0b1000);
        await Assert.That(via.Ca2).IsTrue();

        Read(via, Ora);
        await Assert.That(via.Ca2).IsFalse();

        Idle(via, 3);
        await Assert.That(via.Ca2).IsFalse();

        via.Ca1 = true;
        via.Ca1 = false;
        await Assert.That(via.Ca2).IsTrue();
    }

    [Test]
    public async Task Ca2PulseModeIsLowForExactlyOneCycle()
    {
        var via = Reset();
        Write(via, Pcr, 0b1010);

        Write(via, Ora, 0x12);
        await Assert.That(via.Ca2).IsFalse();

        Idle(via);
        await Assert.That(via.Ca2).IsTrue();
    }

    [Test]
    public async Task Cb2HandshakeFollowsOrbWriteButNotRead()
    {
        var via = Reset();
        Write(via, Pcr, 0b1000_0000);

        Read(via, Orb);
        await Assert.That(via.Cb2).IsTrue();

        Write(via, Orb, 0x01);
        await Assert.That(via.Cb2).IsFalse();

        via.Cb1 = true;
        via.Cb1 = false;
        await Assert.That(via.Cb2).IsTrue();
    }

    [Test]
    public async Task Cb2PulseModeIsLowForOneCycle()
    {
        var via = Reset();
        Write(via, Pcr, 0b1010_0000);

        Write(via, Orb, 0x01);
        await Assert.That(via.Cb2).IsFalse();

        Idle(via);
        await Assert.That(via.Cb2).IsTrue();
    }

    [Test]
    public async Task ManualOutputModesForceCa2AndCb2()
    {
        var via = Reset();

        Write(via, Pcr, 0b1100_1100);
        await Assert.That(via.Ca2).IsFalse();
        await Assert.That(via.Cb2).IsFalse();

        Write(via, Pcr, 0b1110_1110);
        await Assert.That(via.Ca2).IsTrue();
        await Assert.That(via.Cb2).IsTrue();
    }

    [Test]
    public async Task OutputModeIgnoresExternalDriveOfCa2()
    {
        var via = Reset();
        Write(via, Pcr, 0b1110);

        via.Ca2 = false;

        await Assert.That(via.Ca2).IsTrue();
        await Assert.That(Read(via, Ifr)).IsEqualTo((byte)0);
    }

    [Test]
    public async Task InputLatchingHoldsPortAFromTheCa1Edge()
    {
        var via = Reset();
        Write(via, Acr, 0x01);
        via.PA = 0x3C;
        via.Ca1 = true;
        via.Ca1 = false;
        via.PA = 0xC3;

        await Assert.That(Read(via, Ora)).IsEqualTo((byte)0x3C);
    }

    [Test]
    public async Task InputLatchingHoldsPortBFromTheCb1Edge()
    {
        var via = Reset();
        Write(via, Acr, 0x02);
        via.PB = 0x3C;
        via.Cb1 = true;
        via.Cb1 = false;
        via.PB = 0xC3;

        await Assert.That(Read(via, Orb)).IsEqualTo((byte)0x3C);
    }

    [Test]
    public async Task Timer1ReadOfLowCounterClearsFlagButLatchReadDoesNot()
    {
        var via = Reset();
        Write(via, T1Cl, 2);
        Write(via, T1Ch, 0);
        Idle(via, 6);
        await Assert.That(Read(via, Ifr)).IsEqualTo((byte)0x40);

        Read(via, T1Ll);
        await Assert.That(Read(via, Ifr)).IsEqualTo((byte)0x40);

        Read(via, T1Cl);
        await Assert.That(Read(via, Ifr)).IsEqualTo((byte)0);
    }

    [Test]
    public async Task Timer1OneShotFlagsOnlyOnce()
    {
        var via = Reset();
        Write(via, T1Cl, 2);
        Write(via, T1Ch, 0);
        Idle(via, 6);
        Write(via, Ifr, 0x40);

        Idle(via, 20);

        await Assert.That(Read(via, Ifr)).IsEqualTo((byte)0);
    }

    [Test]
    public async Task Timer1FreeRunFlagsEveryPeriod()
    {
        var via = Reset();
        Write(via, Acr, 0x40);
        Write(via, T1Cl, 2);
        Write(via, T1Ch, 0);
        Idle(via, 6);
        Write(via, Ifr, 0x40);

        Idle(via, 4);

        await Assert.That(Read(via, Ifr)).IsEqualTo((byte)0x40);
    }

    [Test]
    public async Task Timer1Pb7GoesLowOnWriteAndHighOnTimeoutInOneShot()
    {
        var via = Reset();
        Write(via, Acr, 0x80);
        Write(via, T1Cl, 3);
        Write(via, T1Ch, 0);

        await Assert.That(via.PortB & 0x80).IsEqualTo(0);
        await Assert.That(via.PbDirection & 0x80).IsEqualTo(0x80);

        Idle(via, 6);

        await Assert.That(via.PortB & 0x80).IsEqualTo(0x80);
    }

    [Test]
    public async Task Timer2PulseCountDecrementsOnPb6FallingEdgesOnly()
    {
        var via = Reset();
        Write(via, Acr, 0x20);
        Write(via, T2Cl, 2);
        Write(via, T2Ch, 0);

        // Time passing without pulses does not count.
        Idle(via, 10);
        await Assert.That(Read(via, Ifr)).IsEqualTo((byte)0);

        for (var i = 0; i < 2; i++)
        {
            via.PB = 0xFF;
            Idle(via);
            via.PB = 0xBF;
            Idle(via);
        }

        await Assert.That(Read(via, Ifr)).IsEqualTo((byte)0);

        via.PB = 0xFF;
        Idle(via);
        via.PB = 0xBF;
        Idle(via);

        await Assert.That(Read(via, Ifr)).IsEqualTo((byte)0x20);
    }

    [Test]
    public async Task ShiftOutUnderPhi2ClockSendsMsbFirstAndFlagsAfterEightBits()
    {
        var via = Reset();
        Write(via, Acr, 0b0001_1000);
        Write(via, Sr, 0b1010_0110);

        var bits = 0;

        for (var i = 0; i < 8; i++)
        {
            // CB1 falls, then CB2 carries the next bit until CB1 rises.
            while (via.Cb1)
            {
                Idle(via);
            }

            bits = (bits << 1) | (via.Cb2 ? 1 : 0);

            while (!via.Cb1)
            {
                Idle(via);
            }
        }

        await Assert.That(bits).IsEqualTo(0b1010_0110);
        await Assert.That(Read(via, Ifr) & 0x04).IsEqualTo(0x04);
        await Assert.That(via.Cb1).IsTrue();
    }

    [Test]
    public async Task ShiftInUnderPhi2ClockSamplesCb2OnRisingEdges()
    {
        var via = Reset();
        Write(via, Acr, 0b0000_1000);
        Read(via, Sr);

        var data = 0b1100_1010;

        for (var i = 7; i >= 0; i--)
        {
            via.Cb2 = ((data >> i) & 1) != 0;

            while (via.Cb1)
            {
                Idle(via);
            }

            while (!via.Cb1)
            {
                Idle(via);
            }
        }

        Idle(via);

        // Reading SR clears the flag, so check the flag first.
        await Assert.That(Read(via, Ifr) & 0x04).IsEqualTo(0x04);
        await Assert.That(Read(via, Sr)).IsEqualTo((byte)data);
    }

    [Test]
    public async Task ShiftOutUnderTimer2ClockUsesTimer2LowByteForTheRate()
    {
        var via = Reset();
        Write(via, Acr, 0b0001_0100);
        Write(via, T2Cl, 2);
        Write(via, T2Ch, 0);
        Write(via, Sr, 0x80);

        // The clock toggles every N+2 = 4 cycles.
        var transitions = 0;
        var previous = via.Cb1;
        var cycles = 0;

        while (transitions < 2 && cycles < 100)
        {
            Idle(via);
            cycles++;

            if (via.Cb1 != previous)
            {
                previous = via.Cb1;
                transitions++;

                if (transitions == 1)
                {
                    cycles = 0;
                }
            }
        }

        await Assert.That(transitions).IsEqualTo(2);
        await Assert.That(cycles).IsEqualTo(4);
    }

    [Test]
    public async Task ShiftOutUnderExternalClockShiftsOnCb1Edges()
    {
        var via = Reset();
        Write(via, Acr, 0b0001_1100);
        via.Cb1 = true;
        Write(via, Sr, 0b1000_0001);

        var bits = 0;

        for (var i = 0; i < 8; i++)
        {
            via.Cb1 = false;
            bits = (bits << 1) | (via.Cb2 ? 1 : 0);
            via.Cb1 = true;
        }

        await Assert.That(bits).IsEqualTo(0b1000_0001);
        await Assert.That(Read(via, Ifr) & 0x04).IsEqualTo(0x04);
    }

    [Test]
    public async Task ReadingOrWritingSrClearsTheShiftFlag()
    {
        var via = Reset();
        Write(via, Acr, 0b0000_1000);
        Read(via, Sr);
        Idle(via, 40);
        await Assert.That(Read(via, Ifr) & 0x04).IsEqualTo(0x04);

        Read(via, Sr);

        await Assert.That(Read(via, Ifr) & 0x04).IsEqualTo(0);
    }
}
