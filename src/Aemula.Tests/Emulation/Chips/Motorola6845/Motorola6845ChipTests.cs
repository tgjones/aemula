using System.Threading.Tasks;
using Aemula.Emulation.Chips.Motorola6845;

namespace Aemula.Tests.Emulation.Chips.Motorola6845;

public class Motorola6845ChipTests
{
    // Mode 4 timing: 64 characters by 312 lines of 8 scanlines' worth of rows, no interlace.
    private static readonly byte[] Mode4 = [0x3F, 0x28, 0x31, 0x24, 0x26, 0x00, 0x20, 0x22, 0x00, 0x07, 0x67, 0x08];

    private static Motorola6845Chip Programmed(byte[] registers)
    {
        var crtc = Motorola6845Bus.Reset();

        for (var register = 0; register < registers.Length; register++)
        {
            Motorola6845Bus.WriteRegister(crtc, register, registers[register]);
        }

        Motorola6845Bus.PulseReset(crtc);
        return crtc;
    }

    [Test]
    [Arguments(0, 0xFF)]
    [Arguments(1, 0xFF)]
    [Arguments(2, 0xFF)]
    [Arguments(3, 0xFF)]
    [Arguments(4, 0x7F)]
    [Arguments(5, 0x1F)]
    [Arguments(6, 0x7F)]
    [Arguments(7, 0x7F)]
    [Arguments(8, 0xF3)]
    [Arguments(9, 0x1F)]
    [Arguments(10, 0x7F)]
    [Arguments(11, 0x1F)]
    [Arguments(12, 0x3F)]
    [Arguments(13, 0xFF)]
    [Arguments(14, 0x3F)]
    [Arguments(15, 0xFF)]
    public async Task WritableRegistersKeepOnlyTheirImplementedBits(int register, int mask)
    {
        var crtc = Motorola6845Bus.Reset();

        Motorola6845Bus.WriteRegister(crtc, register, 0xFF);

        // R0-R11 are write only on the bus; R12-R15 read back.
        var expected = register >= 12 ? (byte)mask : (byte)0;
        await Assert.That(Motorola6845Bus.ReadRegister(crtc, register)).IsEqualTo(expected);
    }

    [Test]
    public async Task AddressRegisterKeepsFiveBitsAndReadsAsZero()
    {
        var crtc = Motorola6845Bus.Reset();

        Motorola6845Bus.WriteRegister(crtc, 0xE0 | 14, 0x12);

        // Selected register is 14, not 0xEE.
        await Assert.That(Motorola6845Bus.Read(crtc, true)).IsEqualTo((byte)0x12);
        await Assert.That(Motorola6845Bus.Read(crtc, false)).IsEqualTo((byte)0);
    }

    [Test]
    public async Task LightPenRegistersAreReadOnly()
    {
        var crtc = Motorola6845Bus.Reset();

        Motorola6845Bus.WriteRegister(crtc, 16, 0x3F);
        Motorola6845Bus.WriteRegister(crtc, 17, 0xFF);

        await Assert.That(Motorola6845Bus.ReadRegister(crtc, 16)).IsEqualTo((byte)0);
        await Assert.That(Motorola6845Bus.ReadRegister(crtc, 17)).IsEqualTo((byte)0);
    }

    [Test]
    public async Task UnselectedChipIgnoresTheBus()
    {
        var crtc = Motorola6845Bus.Reset();

        crtc.Cs = true;
        crtc.RS = false;
        crtc.RW = false;
        crtc.DB = 14;
        crtc.E = true;
        crtc.E = false;
        crtc.RS = true;
        crtc.DB = 0x2A;
        crtc.E = true;
        crtc.E = false;

        await Assert.That(Motorola6845Bus.ReadRegister(crtc, 14)).IsEqualTo((byte)0);
    }

    [Test]
    public async Task LightPenStrobeLatchesMemoryAddressOnRisingEdge()
    {
        var crtc = Programmed(Mode4);
        Motorola6845Bus.WriteRegister(crtc, 12, 0x05);
        Motorola6845Bus.WriteRegister(crtc, 13, 0x00);
        Motorola6845Bus.PulseReset(crtc);

        for (var i = 0; i < 10; i++)
        {
            Motorola6845Bus.Tick(crtc);
        }

        var address = crtc.MA;
        crtc.LpStb = true;
        Motorola6845Bus.Tick(crtc);
        crtc.LpStb = false;

        await Assert.That(address).IsEqualTo((ushort)0x050A);
        await Assert.That(Motorola6845Bus.ReadRegister(crtc, 16)).IsEqualTo((byte)0x05);
        await Assert.That(Motorola6845Bus.ReadRegister(crtc, 17)).IsEqualTo((byte)0x0A);
    }

    [Test]
    public async Task LightPenRegistersHoldUntilNextStrobe()
    {
        var crtc = Programmed(Mode4);

        Motorola6845Bus.Tick(crtc);
        crtc.LpStb = true;
        crtc.LpStb = false;

        for (var i = 0; i < 50; i++)
        {
            Motorola6845Bus.Tick(crtc);
        }

        await Assert.That(Motorola6845Bus.ReadRegister(crtc, 17)).IsEqualTo((byte)1);
    }

    [Test]
    public async Task HeldInResetTheCountersDoNotAdvance()
    {
        var crtc = Programmed(Mode4);

        for (var i = 0; i < 5; i++)
        {
            Motorola6845Bus.Tick(crtc);
        }

        crtc.Res = false;

        for (var i = 0; i < 5; i++)
        {
            Motorola6845Bus.Tick(crtc);
        }

        await Assert.That(crtc.MA).IsEqualTo((ushort)0);
        await Assert.That(crtc.RA).IsEqualTo((byte)0);
    }

    [Test]
    public async Task ResetKeepsTheRegisters()
    {
        var crtc = Motorola6845Bus.Reset();
        Motorola6845Bus.WriteRegister(crtc, 15, 0x77);

        Motorola6845Bus.PulseReset(crtc);

        await Assert.That(Motorola6845Bus.ReadRegister(crtc, 15)).IsEqualTo((byte)0x77);
    }

    [Test]
    public async Task NonInterlacedFrameIsOneRowCountLong()
    {
        // (R4 + 1) rows of (R9 + 1) lines plus R5 adjust lines, each R0 + 1 characters.
        var crtc = Programmed(Mode4);
        var frameClocks = (0x26 + 1) * (7 + 1) * (0x3F + 1);
        var vsyncStarts = 0;
        var firstStart = -1;
        var previousVsync = false;

        for (var clock = 0; clock < frameClocks * 3; clock++)
        {
            if (crtc.VSync && !previousVsync)
            {
                vsyncStarts++;
                firstStart = firstStart < 0 ? clock : firstStart;
                await Assert.That((clock - firstStart) % frameClocks).IsEqualTo(0);
            }

            previousVsync = crtc.VSync;
            Motorola6845Bus.Tick(crtc);
        }

        await Assert.That(vsyncStarts).IsEqualTo(3);
    }

    [Test]
    public async Task InterlaceSyncGivesVsyncsHalfALineApartFromWholeFields()
    {
        // R8 = 1: each field is 312.5 lines, so vsyncs come 64 * 312.5 = 20000
        // characters apart.
        byte[] registers = [0x3F, 0x28, 0x31, 0x24, 0x26, 0x00, 0x20, 0x22, 0x01, 0x07, 0x67, 0x08];
        var crtc = Programmed(registers);
        var starts = new System.Collections.Generic.List<int>();
        var previousVsync = false;

        for (var clock = 0; clock < 20000 * 5; clock++)
        {
            if (crtc.VSync && !previousVsync)
            {
                starts.Add(clock);
            }

            previousVsync = crtc.VSync;
            Motorola6845Bus.Tick(crtc);
        }

        await Assert.That(starts[2] - starts[1]).IsEqualTo(20000);
        await Assert.That(starts[3] - starts[2]).IsEqualTo(20000);
    }

    [Test]
    public async Task InterlaceSyncAndVideoStepsRasterAddressByTwoWithFieldNumberInBitZero()
    {
        byte[] registers = [0x3F, 0x28, 0x31, 0x24, 0x26, 0x00, 0x20, 0x22, 0x03, 0x07, 0x67, 0x08];
        var crtc = Programmed(registers);

        var rasters = new System.Collections.Generic.List<byte>();

        for (var line = 0; line < 4; line++)
        {
            rasters.Add(crtc.RA);

            for (var i = 0; i < 64; i++)
            {
                Motorola6845Bus.Tick(crtc);
            }
        }

        await Assert.That(rasters).IsEquivalentTo(new byte[] { 0, 2, 4, 6 });
    }

    [Test]
    public async Task CursorShowsOnItsCharacterAndRasterLinesOnly()
    {
        // Steady cursor (R10 bits 5-6 = 0) on the third character of the
        // second row, scanlines 2 to 4.
        byte[] registers = [19, 12, 15, 0x32, 5, 3, 4, 5, 0x00, 7, 0x02, 0x04, 0x00, 0x00, 0x00, 12 + 2];
        var crtc = Programmed(registers);

        var cursorClocks = new System.Collections.Generic.List<(int Row, int Line)>();

        for (var clock = 0; clock < 20 * 8 * 6; clock++)
        {
            if (crtc.Cursor)
            {
                cursorClocks.Add((clock / (20 * 8), crtc.RA));
            }

            Motorola6845Bus.Tick(crtc);
        }

        // Row 1's address is 12, so character 14 is its third cell.
        await Assert.That(cursorClocks).IsEquivalentTo(new[] { (1, 2), (1, 3), (1, 4) });
    }
}
