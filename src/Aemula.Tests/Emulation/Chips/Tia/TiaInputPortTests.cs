using System.Threading.Tasks;
using Aemula.Emulation.Chips.Tia;

namespace Aemula.Tests.Emulation.Chips.Tia;

// TIA input-port read decode (INPT0-INPT5, 0x38-0x3D). A bare TiaChip
// (CS1-selected, Phi2-pulsed for register access, Osc-pulsed for colour
// clocks) - same harness shape as TiaCollisionTests / TiaBallTests.
//
// The ports drive D7 only via Data67 (bit 1 -> D7, bit 0 -> D6); the system
// merges Data67 << 6 onto the CPU bus and supplies D0-D5 itself. So a read
// that latches D7 low leaves Data67 == 0b00, a read that drives D7 high
// leaves Data67 == 0b10, and D6 (bit 0) is never set.
public class TiaInputPortTests
{
    private const byte Vblank = 0x01;

    private const byte Inpt0 = 0x38;
    private const byte Inpt1 = 0x39;
    private const byte Inpt2 = 0x3A;
    private const byte Inpt3 = 0x3B;
    private const byte Inpt4 = 0x3C;
    private const byte Inpt5 = 0x3D;

    // VBLANK payloads: D6 (Data67 bit 0) enables the I4/I5 latches, D7
    // (Data67 bit 1) dumps I0-I3 to ground.
    private const byte I45LatchEnable = 0b0100_0000;
    private const byte I03DumpToGround = 0b1000_0000;

    private const byte D7 = 0b10;
    private const byte NoDrive = 0b00;

    private static TiaChip NewTia() => new() { CS1 = true };

    private static void Write(TiaChip tia, byte address, byte data)
    {
        tia.RW = false;
        tia.Address = address;
        tia.Data05 = (byte)(data & 0x3F);
        tia.Data67 = (byte)(data >> 6);
        tia.Phi2 = false;
        tia.Phi2 = true;
    }

    // Perform a register read edge and return TIA's Data67 (the value the
    // system ORs onto D6/D7 of the CPU bus).
    private static byte ReadData67(TiaChip tia, byte address)
    {
        tia.RW = true;
        tia.Address = address;
        tia.Phi2 = false;
        tia.Phi2 = true;
        return tia.Data67;
    }

    [Test]
    public async Task Inpt0To3PassTheirIBitThroughOnD7()
    {
        var tia = NewTia();

        var ports = new[] { Inpt0, Inpt1, Inpt2, Inpt3 };
        for (var bit = 0; bit < 4; bit++)
        {
            tia.I = (byte)(1 << bit);
            await Assert.That(ReadData67(tia, ports[bit])).IsEqualTo(D7);

            tia.I = 0x00;
            await Assert.That(ReadData67(tia, ports[bit])).IsEqualTo(NoDrive);
        }
    }

    [Test]
    public async Task Inpt0To3ReadZeroWhileDumpedToGround()
    {
        var tia = NewTia();
        Write(tia, Vblank, I03DumpToGround);
        tia.I = 0x0F; // I0-I3 all "charged".

        await Assert.That(ReadData67(tia, Inpt0)).IsEqualTo(NoDrive);
        await Assert.That(ReadData67(tia, Inpt1)).IsEqualTo(NoDrive);
        await Assert.That(ReadData67(tia, Inpt2)).IsEqualTo(NoDrive);
        await Assert.That(ReadData67(tia, Inpt3)).IsEqualTo(NoDrive);

        // Releasing the dump lets the I bits show through again.
        Write(tia, Vblank, 0x00);
        await Assert.That(ReadData67(tia, Inpt0)).IsEqualTo(D7);
        await Assert.That(ReadData67(tia, Inpt3)).IsEqualTo(D7);
    }

    [Test]
    public async Task Inpt4And5PassThroughWhileLatchingDisabled()
    {
        var tia = NewTia();

        tia.I = 0b0011_0000; // I4, I5 high (trigger not pressed).
        await Assert.That(ReadData67(tia, Inpt4)).IsEqualTo(D7);
        await Assert.That(ReadData67(tia, Inpt5)).IsEqualTo(D7);

        tia.I = 0b0000_0000; // Both pressed.
        await Assert.That(ReadData67(tia, Inpt4)).IsEqualTo(NoDrive);
        await Assert.That(ReadData67(tia, Inpt5)).IsEqualTo(NoDrive);

        // No latching, so releasing restores the high level immediately.
        tia.I = 0b0011_0000;
        await Assert.That(ReadData67(tia, Inpt4)).IsEqualTo(D7);
        await Assert.That(ReadData67(tia, Inpt5)).IsEqualTo(D7);
    }

    [Test]
    public async Task Inpt4LatchesLowAndHoldsOncePinHasGoneLow()
    {
        var tia = NewTia();
        tia.I = 0b0011_0000; // Triggers idle high before latching is armed.
        Write(tia, Vblank, I45LatchEnable);

        await Assert.That(ReadData67(tia, Inpt4)).IsEqualTo(D7);

        tia.I = 0b0010_0000; // I4 low.
        await Assert.That(ReadData67(tia, Inpt4)).IsEqualTo(NoDrive);

        tia.I = 0b0011_0000; // I4 back high - latch still holds low.
        await Assert.That(ReadData67(tia, Inpt4)).IsEqualTo(NoDrive);
    }

    [Test]
    public async Task Inpt4CatchesAMomentaryLowPulseBetweenReads()
    {
        var tia = NewTia();
        tia.I = 0b0011_0000; // Triggers idle high before latching is armed.
        Write(tia, Vblank, I45LatchEnable);

        await Assert.That(ReadData67(tia, Inpt4)).IsEqualTo(D7);

        // Press and release entirely between the two reads.
        tia.I = 0b0010_0000;
        tia.I = 0b0011_0000;

        await Assert.That(ReadData67(tia, Inpt4)).IsEqualTo(NoDrive);
    }

    [Test]
    public async Task ClearingVblankD6ReleasesTheTriggerLatches()
    {
        var tia = NewTia();
        tia.I = 0b0011_0000; // Triggers idle high before latching is armed.
        Write(tia, Vblank, I45LatchEnable);

        tia.I = 0b0000_0000; // I4 + I5 low - both latch.
        tia.I = 0b0011_0000; // Back high.
        await Assert.That(ReadData67(tia, Inpt4)).IsEqualTo(NoDrive);
        await Assert.That(ReadData67(tia, Inpt5)).IsEqualTo(NoDrive);

        Write(tia, Vblank, 0x00); // Latching disabled - latches reset.
        await Assert.That(ReadData67(tia, Inpt4)).IsEqualTo(D7);
        await Assert.That(ReadData67(tia, Inpt5)).IsEqualTo(D7);
    }

    [Test]
    public async Task EnablingLatchingWithThePinAlreadyLowLatchesImmediately()
    {
        var tia = NewTia();
        tia.I = 0b0010_0000; // I4 low, I5 high, before latching is armed.

        Write(tia, Vblank, I45LatchEnable);
        tia.I = 0b0011_0000; // Release both.

        await Assert.That(ReadData67(tia, Inpt4)).IsEqualTo(NoDrive);
        await Assert.That(ReadData67(tia, Inpt5)).IsEqualTo(D7);
    }

    [Test]
    public async Task InputDecodeDrivesD7OnlyAndLeavesD6Clear()
    {
        var tia = NewTia();
        tia.I = 0xFF;

        // Every port either drives D7 (0b10) or nothing (0b00) - bit 0 (D6)
        // is never set.
        foreach (var port in new[] { Inpt0, Inpt1, Inpt2, Inpt3, Inpt4, Inpt5 })
        {
            var data67 = ReadData67(tia, port);
            await Assert.That(data67 & 0b01).IsEqualTo(0);
            await Assert.That(data67).IsEqualTo(D7);
        }
    }

    [Test]
    public async Task ReadOutsideDrivenRangeLeavesData67Untouched()
    {
        var tia = NewTia();

        tia.Data67 = 0b11;
        await Assert.That(ReadData67(tia, 0x3E)).IsEqualTo((byte)0b11);
        await Assert.That(ReadData67(tia, 0x3F)).IsEqualTo((byte)0b11);
        await Assert.That(ReadData67(tia, 0x00)).IsEqualTo((byte)0b11);
    }
}
