using System.Collections.Generic;
using System.Threading.Tasks;
using Aemula.Emulation.Chips.Tia;

namespace Aemula.Tests.Emulation.Chips.Tia;

// Drives the full colour-clock machinery through Osc and reads TiaChip.Col
// per visible pixel, because these cases are about horizontal position: which
// PF cell lights which pixel, whether the right half is mirrored, and how
// many colour clocks the visible region actually spans.
//
// PF cell 0..19 of a half maps to the merged 20-bit playfield word bit 19..0
// (bit 19 first). PF0 fills bits 19..16, PF1 bits 15..8, PF2 bits 7..0. Each
// cell is 4 colour clocks wide, so the left half is scan indices 0..79 and
// the mirrored/repeated right half is 80..159.
public class TiaPlayfieldTests
{
    private const byte Colupf = 0x08;
    private const byte Colubk = 0x09;
    private const byte Ctrlpf = 0x0A;
    private const byte Pf0 = 0x0D;
    private const byte Pf1 = 0x0E;
    private const byte Pf2 = 0x0F;

    private const byte CtrlpfReflect = 0b0000_0001; // D0

    private const int HuePf = 0x4;
    private const int HueBk = 0x7;

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

    private static byte ColorLuma(int hue, int luma) => (byte)((hue << 4) | (luma << 1));

    private static void Tick(TiaChip tia)
    {
        tia.Osc = false;
        tia.Osc = true;

        // The system renders each colour clock after applying that tick's
        // 6507 bus write; mirror that here so a Write() before this Tick() is
        // visible on the pixel this Tick() produces.
        tia.RenderColorClock();
    }

    private static void NextVisibleLineStart(TiaChip tia)
    {
        while (!tia.Blk)
        {
            Tick(tia);
        }

        while (tia.Blk)
        {
            Tick(tia);
        }
    }

    private static byte[] ScanLineColours(TiaChip tia)
    {
        NextVisibleLineStart(tia);

        var colours = new List<byte>();
        while (!tia.Blk)
        {
            colours.Add(tia.Col);
            Tick(tia);
        }

        return colours.ToArray();
    }

    private static void SetColours(TiaChip tia)
    {
        Write(tia, Colupf, ColorLuma(HuePf, 4));
        Write(tia, Colubk, ColorLuma(HueBk, 0));
    }

    // Lets the playfield machinery run a couple of lines after the register
    // writes, then returns a clean scanned line.
    private static byte[] SettledLine(TiaChip tia)
    {
        ScanLineColours(tia);
        ScanLineColours(tia);
        return ScanLineColours(tia);
    }

    [Test]
    public async Task Pf0BitsRenderInTheLeftmost16PixelsOfEachHalf()
    {
        var tia = NewTia();
        SetColours(tia);
        Write(tia, Ctrlpf, 0);

        // PF0 D4-D7 all set -> merged bits 19..16 -> PF cells 0..3 of each
        // half. PF1/PF2 clear, so cells 4..19 are background.
        Write(tia, Pf0, 0xFF);
        Write(tia, Pf1, 0x00);
        Write(tia, Pf2, 0x00);

        var line = SettledLine(tia);

        await Assert.That(line.Length).IsEqualTo(160);

        // Left half: first 16 colour clocks are the playfield colour...
        for (var i = 0; i < 16; i++)
        {
            await Assert.That(line[i]).IsEqualTo((byte)HuePf);
        }

        // ...and the rest of the left half is background.
        for (var i = 16; i < 80; i++)
        {
            await Assert.That(line[i]).IsEqualTo((byte)HueBk);
        }

        // Right half (reflect off) repeats the same pattern.
        for (var i = 80; i < 96; i++)
        {
            await Assert.That(line[i]).IsEqualTo((byte)HuePf);
        }
    }

    [Test]
    public async Task Pf0BitsAreBackgroundWhenPf0IsClear()
    {
        var tia = NewTia();
        SetColours(tia);
        Write(tia, Ctrlpf, 0);

        Write(tia, Pf0, 0x00);
        Write(tia, Pf1, 0x00);
        Write(tia, Pf2, 0x00);

        var line = SettledLine(tia);

        for (var i = 0; i < 16; i++)
        {
            await Assert.That(line[i]).IsEqualTo((byte)HueBk);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RightHalfIsMirroredOnlyWhenReflectIsSet(bool reflect)
    {
        var tia = NewTia();
        SetColours(tia);
        Write(tia, Ctrlpf, reflect ? CtrlpfReflect : (byte)0);

        // Only PF0 (merged bits 19..16) is lit: PF cells 0..3 of a
        // non-reflected half, or cells 16..19 of a reflected right half.
        Write(tia, Pf0, 0xFF);
        Write(tia, Pf1, 0x00);
        Write(tia, Pf2, 0x00);

        var line = SettledLine(tia);

        await Assert.That(line.Length).IsEqualTo(160);

        // The left half is never mirrored: cells 0..3 (pixels 0..15) light
        // regardless of the reflect bit.
        for (var i = 0; i < 16; i++)
        {
            await Assert.That(line[i]).IsEqualTo((byte)HuePf);
        }

        for (var i = 16; i < 80; i++)
        {
            await Assert.That(line[i]).IsEqualTo((byte)HueBk);
        }

        if (reflect)
        {
            // Mirrored: the lit cells move to the far right edge (pixels
            // 144..159), and the half's start is background.
            for (var i = 80; i < 144; i++)
            {
                await Assert.That(line[i]).IsEqualTo((byte)HueBk);
            }

            for (var i = 144; i < 160; i++)
            {
                await Assert.That(line[i]).IsEqualTo((byte)HuePf);
            }
        }
        else
        {
            // Repeated: same as the left half.
            for (var i = 80; i < 96; i++)
            {
                await Assert.That(line[i]).IsEqualTo((byte)HuePf);
            }

            for (var i = 96; i < 160; i++)
            {
                await Assert.That(line[i]).IsEqualTo((byte)HueBk);
            }
        }
    }

    [Test]
    public async Task VisibleRegionIsExactly160ColourClocks()
    {
        var tia = NewTia();
        SetColours(tia);
        Write(tia, Ctrlpf, 0);

        // A few lines with no HMOVE anywhere: the active region should be the
        // 160 playfield pixels and nothing more (it used to leak 4 extra
        // background clocks past pixel 160 before horizontal blank re-armed).
        ScanLineColours(tia);
        await Assert.That(ScanLineColours(tia).Length).IsEqualTo(160);
        await Assert.That(ScanLineColours(tia).Length).IsEqualTo(160);
        await Assert.That(ScanLineColours(tia).Length).IsEqualTo(160);
    }
}
