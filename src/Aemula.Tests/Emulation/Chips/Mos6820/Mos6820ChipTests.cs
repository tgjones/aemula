using System.Threading.Tasks;
using Aemula.Emulation.Chips.Mos6820;

namespace Aemula.Tests.Emulation.Chips.Mos6820;

// Exercises the PIA through its real bus pins (chip selects, register
// selects, R/W, E edges) rather than any internal register accessor,
// mirroring the datasheet's own function tables for CA1/CA2/CB1/CB2.
public class Mos6820ChipTests
{
    private static void Select(Mos6820Chip pia)
    {
        pia.Cs0 = true;
        pia.Cs1 = true;
        pia.Cs2 = false;
    }

    private static byte Read(Mos6820Chip pia, bool rs1, bool rs0)
    {
        Select(pia);
        pia.Rs1 = rs1;
        pia.Rs0 = rs0;
        pia.RW = true;
        pia.E = true;
        var value = pia.DB;
        pia.E = false;
        return value;
    }

    private static void Write(Mos6820Chip pia, bool rs1, bool rs0, byte value)
    {
        Select(pia);
        pia.Rs1 = rs1;
        pia.Rs0 = rs0;
        pia.RW = false;
        pia.DB = value;
        pia.E = true;
        pia.E = false;
    }

    private static byte ReadCra(Mos6820Chip pia) => Read(pia, false, true);
    private static byte ReadCrb(Mos6820Chip pia) => Read(pia, true, true);
    private static void WriteCra(Mos6820Chip pia, byte value) => Write(pia, false, true, value);
    private static void WriteCrb(Mos6820Chip pia, byte value) => Write(pia, true, true, value);

    private static byte ReadSideA(Mos6820Chip pia) => Read(pia, false, false);
    private static byte ReadSideB(Mos6820Chip pia) => Read(pia, true, false);
    private static void WriteSideA(Mos6820Chip pia, byte value) => Write(pia, false, false, value);
    private static void WriteSideB(Mos6820Chip pia, byte value) => Write(pia, true, false, value);

    [Test]
    public async Task DataDirectionAndPeripheralRegisterShareOneAddressGatedByControlBit2()
    {
        var pia = new Mos6820Chip();

        // CRA bit 2 starts clear, so RS0=0 addresses DDRA.
        WriteSideA(pia, 0b1111_0000);

        // Select the peripheral register (CRA bit 2 = 1) and load it.
        WriteCra(pia, 0x04);
        WriteSideA(pia, 0b1010_0000);

        pia.PA = 0b0000_1111; // external device drives the input-configured bits

        await Assert.That(pia.PortA).IsEqualTo((byte)0b1010_1111);
    }

    [Test]
    public async Task ReadingDdrReturnsWhatWasWritten()
    {
        var pia = new Mos6820Chip();

        WriteSideA(pia, 0xAA);
        WriteSideB(pia, 0x55);

        await Assert.That(ReadSideA(pia)).IsEqualTo((byte)0xAA);
        await Assert.That(ReadSideB(pia)).IsEqualTo((byte)0x55);
    }

    [Test]
    public async Task ControlRegisterWritesCannotChangeFlagBits()
    {
        var pia = new Mos6820Chip();
        WriteCra(pia, 0x01); // IRQ1 enabled, negative edge selected
        pia.Ca1 = true;
        pia.Ca1 = false; // falling edge sets bit 7

        await Assert.That((ReadCra(pia) & 0x80) != 0).IsTrue();

        WriteCra(pia, 0x00); // only bits 0-5 are writable; bit 7 can't be cleared this way

        await Assert.That((ReadCra(pia) & 0x80) != 0).IsTrue();
    }

    [Test]
    public async Task ReadingPeripheralRegisterClearsBothFlagBits()
    {
        var pia = new Mos6820Chip();
        WriteCra(pia, 0x01);
        pia.Ca1 = true;
        pia.Ca1 = false;
        await Assert.That((ReadCra(pia) & 0x80) != 0).IsTrue();

        WriteCra(pia, 0x05); // bit 2 = 1 selects the peripheral register from here on
        ReadSideA(pia);

        await Assert.That((ReadCra(pia) & 0x80) != 0).IsFalse();
    }

    [Test]
    [Arguments(false, false)] // IRQ1 disabled: flag sets, IRQA stays masked (high)
    [Arguments(true, true)]   // IRQ1 enabled: flag sets and IRQA asserts (low)
    public async Task Ca1NegativeEdgeSetsFlagAndGatesIrqOnTheEnableBit(bool irq1Enabled, bool expectIrqAsserted)
    {
        var pia = new Mos6820Chip();
        WriteCra(pia, (byte)(irq1Enabled ? 0x01 : 0x00)); // bit 1 = 0 selects the negative edge

        pia.Ca1 = true;
        await Assert.That(pia.Irqa).IsTrue();

        pia.Ca1 = false; // falling edge - active

        await Assert.That((ReadCra(pia) & 0x80) != 0).IsTrue();
        await Assert.That(pia.Irqa).IsEqualTo(!expectIrqAsserted);
    }

    [Test]
    public async Task Ca1IgnoresTheTransitionOppositeToTheConfiguredEdge()
    {
        var pia = new Mos6820Chip();
        WriteCra(pia, 0x01); // bit 1 = 0: only the negative edge is active

        pia.Ca1 = true; // rising edge - not the configured one

        await Assert.That((ReadCra(pia) & 0x80) != 0).IsFalse();
        await Assert.That(pia.Irqa).IsTrue();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, true)]
    public async Task Ca2InputModeMirrorsCa1sShapeOnBits43(bool irq2Enabled, bool expectIrqAsserted)
    {
        var pia = new Mos6820Chip();
        // Bit 5 = 0 keeps CA2 an input; bit 4 = 0 selects the negative edge.
        WriteCra(pia, (byte)(irq2Enabled ? 0x08 : 0x00));

        pia.Ca2 = true;
        await Assert.That(pia.Irqa).IsTrue();

        pia.Ca2 = false;

        await Assert.That((ReadCra(pia) & 0x40) != 0).IsTrue();
        await Assert.That(pia.Irqa).IsEqualTo(!expectIrqAsserted);
    }

    [Test]
    public async Task Ca2HandshakeModeDropsAfterAPeripheralRegisterAReadAndRestoresOnCa1()
    {
        var pia = new Mos6820Chip();
        // Bit 2 = 1 addresses the peripheral register from here on; bits 5,4,3 = 1,0,0 select handshake mode.
        WriteCra(pia, 0x24);

        await Assert.That(pia.Ca2).IsTrue(); // normally high

        ReadSideA(pia); // "read A side data" - CA2 drops on this read's E falling edge

        await Assert.That(pia.Ca2).IsFalse();

        pia.Ca1 = true;
        pia.Ca1 = false; // CA1's active (negative) edge restores CA2

        await Assert.That(pia.Ca2).IsTrue();
    }

    [Test]
    public async Task Ca2PulseModeAutoRestoresOneEPulseLaterWithoutCa1()
    {
        var pia = new Mos6820Chip();
        // Bits 5,4,3 = 1,0,1 select pulse mode.
        WriteCra(pia, 0x2C);

        ReadSideA(pia);
        await Assert.That(pia.Ca2).IsFalse();

        pia.Cs0 = false; // deselect so the next E toggle doesn't start another bus cycle
        pia.E = true;
        pia.E = false;

        await Assert.That(pia.Ca2).IsTrue();
    }

    [Test]
    public async Task Ca2AlwaysLowMode()
    {
        var pia = new Mos6820Chip();
        WriteCra(pia, 0x30); // bits 5,4,3 = 1,1,0

        await Assert.That(pia.Ca2).IsFalse();
    }

    [Test]
    public async Task Ca2AlwaysHighMode()
    {
        var pia = new Mos6820Chip();
        WriteCra(pia, 0x38); // bits 5,4,3 = 1,1,1

        await Assert.That(pia.Ca2).IsTrue();
    }

    [Test]
    public async Task ExternalWritesToCa2AreIgnoredWhileItsConfiguredAsAnOutput()
    {
        var pia = new Mos6820Chip();
        WriteCra(pia, 0x30); // always-low output mode

        pia.Ca2 = true; // an external device driving CA2 shouldn't matter now

        await Assert.That(pia.Ca2).IsFalse();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, true)]
    public async Task Cb1NegativeEdgeSetsFlagAndGatesIrqOnTheEnableBit(bool irq1Enabled, bool expectIrqAsserted)
    {
        var pia = new Mos6820Chip();
        WriteCrb(pia, (byte)(irq1Enabled ? 0x01 : 0x00));

        pia.Cb1 = true;
        pia.Cb1 = false;

        await Assert.That((ReadCrb(pia) & 0x80) != 0).IsTrue();
        await Assert.That(pia.Irqb).IsEqualTo(!expectIrqAsserted);
    }

    [Test]
    public async Task Cb2HandshakeModeDropsOnTheNextERisingEdgeAfterAnOrbWriteAndRestoresOnCb1()
    {
        var pia = new Mos6820Chip();
        WriteCrb(pia, 0x24); // bit 2 = 1 addresses ORB; bits 5,4,3 = 1,0,0 handshake

        await Assert.That(pia.Cb2).IsTrue();

        WriteSideB(pia, 0x00); // "write B side data" - commits on this call's E falling edge, arms the strobe
        await Assert.That(pia.Cb2).IsTrue(); // not yet - it's the *next* E rising edge that drops it

        pia.E = true;
        await Assert.That(pia.Cb2).IsFalse();

        pia.Cs0 = false; // deselect so the matching falling edge below doesn't start another bus cycle
        pia.E = false;

        pia.Cb1 = true;
        pia.Cb1 = false;

        await Assert.That(pia.Cb2).IsTrue();
    }

    [Test]
    public async Task Cb2PulseModeAutoRestoresOneEPulseLaterWithoutCb1()
    {
        var pia = new Mos6820Chip();
        WriteCrb(pia, 0x2C); // bits 5,4,3 = 1,0,1 pulse mode

        WriteSideB(pia, 0x00);
        pia.E = true; // drops CB2, arms the auto-restore for the next rising edge

        await Assert.That(pia.Cb2).IsFalse();

        pia.Cs0 = false;
        pia.E = false;
        pia.E = true;

        await Assert.That(pia.Cb2).IsTrue();
    }

    [Test]
    public async Task Cb2AlwaysLowMode()
    {
        var pia = new Mos6820Chip();
        WriteCrb(pia, 0x30);

        await Assert.That(pia.Cb2).IsFalse();
    }

    [Test]
    public async Task Cb2AlwaysHighMode()
    {
        var pia = new Mos6820Chip();
        WriteCrb(pia, 0x38);

        await Assert.That(pia.Cb2).IsTrue();
    }

    [Test]
    public async Task ResetClearsAllRegisters()
    {
        var pia = new Mos6820Chip();
        pia.Res = true; // idle high baseline

        WriteSideA(pia, 0xFF);
        WriteCra(pia, 0x3F);
        WriteSideB(pia, 0xFF);
        WriteCrb(pia, 0x3F);

        pia.Res = false;
        pia.Res = true; // registers clear on this rising edge

        await Assert.That(ReadCra(pia)).IsEqualTo((byte)0);
        await Assert.That(ReadCrb(pia)).IsEqualTo((byte)0);
        await Assert.That(ReadSideA(pia)).IsEqualTo((byte)0); // CRA bit 2 was cleared too, so this reads DDRA
        await Assert.That(ReadSideB(pia)).IsEqualTo((byte)0);
    }

    [Test]
    public async Task RegisterAccessIsIgnoredWhenNotSelected()
    {
        var pia = new Mos6820Chip();

        pia.Cs0 = false;
        pia.Cs1 = true;
        pia.Cs2 = false;
        pia.Rs1 = false;
        pia.Rs0 = false;
        pia.RW = false;
        pia.DB = 0xFF;
        pia.E = true;
        pia.E = false;

        await Assert.That(ReadSideA(pia)).IsEqualTo((byte)0);
    }
}
