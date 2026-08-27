using System.Collections.Generic;
using System.Threading.Tasks;
using Aemula.Emulation.Chips.Tia;

namespace Aemula.Tests.Emulation.Chips.Tia;

// Unit-level NUSIZ player-scaling tests, same harness shape as
// TiaMissileTests / TiaBallTests: Osc pulses step the colour-clock machinery
// and register writes go straight in via a CS1-selected Phi2 edge. Each test
// gives player 0 a known graphic, strobes RESP0 partway into a line, lets the
// player counter settle over two lines, then scans a whole visible region
// reading PlayerAndMissile0.PixelOn per colour clock.
//
// NUSIZ 5 (double) and 7 (quad) do not add copies - they hold each of the 8
// graphic bits on screen for 2 / 4 colour clocks. Widths and run counts are
// asserted, not absolute columns (those depend on TIA's internal counter
// phase), so the graphic is placed with an isolated bit or a gapped pattern.
public class TiaNusizScalingTests
{
    private const byte Nusiz0 = 0x04;
    private const byte Colup0 = 0x06;
    private const byte Refp0 = 0x0B;
    private const byte Resp0 = 0x10;
    private const byte Grp0 = 0x1B;

    private const byte ReflectD3 = 0b1000;

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

    // One master OSC pulse == one colour clock.
    private static void Tick(TiaChip tia)
    {
        tia.Osc = false;
        tia.Osc = true;
    }

    // Advance to the first visible (non-blank) colour clock of the next line.
    private static void NextLine(TiaChip tia)
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

    // Scan one whole visible region, returning player 0's lit flag for each
    // colour clock in it.
    private static bool[] ScanPlayer0(TiaChip tia)
    {
        NextLine(tia);

        var lit = new List<bool>();
        while (!tia.Blk)
        {
            lit.Add(tia.PlayerAndMissile0.PixelOn);
            Tick(tia);
        }

        return lit.ToArray();
    }

    // Strobe RESP0 atX colour clocks into a visible line, then let the player
    // counter self-settle over two more lines so it sits at a stable column
    // on the lines scanned afterwards.
    private static void PositionPlayer0(TiaChip tia, int atX)
    {
        NextLine(tia);
        for (var i = 0; i < atX; i++)
        {
            Tick(tia);
        }

        Write(tia, Resp0, 0);

        NextLine(tia);
        NextLine(tia);
    }

    private static int FirstLit(bool[] scan)
    {
        for (var i = 0; i < scan.Length; i++)
        {
            if (scan[i])
            {
                return i;
            }
        }

        return -1;
    }

    private static int LitCount(bool[] scan)
    {
        var n = 0;
        foreach (var b in scan)
        {
            if (b)
            {
                n++;
            }
        }

        return n;
    }

    // Number of separate lit runs (player copies, or gapped graphic bits).
    private static int RunCount(bool[] scan)
    {
        var runs = 0;
        var prev = false;
        foreach (var b in scan)
        {
            if (b && !prev)
            {
                runs++;
            }

            prev = b;
        }

        return runs;
    }

    // Longest contiguous lit run in a scan.
    private static int LongestRun(bool[] scan)
    {
        var best = 0;
        var current = 0;
        foreach (var b in scan)
        {
            current = b ? current + 1 : 0;
            if (current > best)
            {
                best = current;
            }
        }

        return best;
    }

    [Test]
    public async Task Nusiz0DrawsEachGraphicBitAsOnePixel()
    {
        var tia = NewTia();
        Write(tia, Nusiz0, 0x00);
        Write(tia, Colup0, 0x00);
        Write(tia, Grp0, 0x80); // single bit -> single one-pixel run
        PositionPlayer0(tia, 40);

        var scan = ScanPlayer0(tia);

        await Assert.That(RunCount(scan)).IsEqualTo(1);
        await Assert.That(LongestRun(scan)).IsEqualTo(1);
        await Assert.That(LitCount(scan)).IsEqualTo(1);
    }

    [Test]
    public async Task Nusiz5HoldsEachGraphicBitForTwoPixels()
    {
        var tia = NewTia();
        Write(tia, Colup0, 0x00);
        Write(tia, Grp0, 0b1010_1010); // four isolated bits
        PositionPlayer0(tia, 40);

        Write(tia, Nusiz0, 0x00);
        var normal = ScanPlayer0(tia);

        Write(tia, Nusiz0, 0x05);
        var doubled = ScanPlayer0(tia);

        // Same number of runs (one per set bit, single copy), each run twice
        // as wide, so twice the lit pixels overall.
        await Assert.That(RunCount(normal)).IsEqualTo(4);
        await Assert.That(RunCount(doubled)).IsEqualTo(4);
        await Assert.That(LongestRun(normal)).IsEqualTo(1);
        await Assert.That(LongestRun(doubled)).IsEqualTo(2);
        await Assert.That(LitCount(doubled)).IsEqualTo(LitCount(normal) * 2);
    }

    [Test]
    public async Task Nusiz7HoldsEachGraphicBitForFourPixels()
    {
        var tia = NewTia();
        Write(tia, Colup0, 0x00);
        Write(tia, Grp0, 0b1010_1010);
        PositionPlayer0(tia, 40);

        Write(tia, Nusiz0, 0x00);
        var normal = ScanPlayer0(tia);

        Write(tia, Nusiz0, 0x07);
        var quad = ScanPlayer0(tia);

        await Assert.That(RunCount(quad)).IsEqualTo(4);
        await Assert.That(LongestRun(normal)).IsEqualTo(1);
        await Assert.That(LongestRun(quad)).IsEqualTo(4);
        await Assert.That(LitCount(quad)).IsEqualTo(LitCount(normal) * 4);
    }

    [Test]
    public async Task StretchModesTotalWidthIsEightSixteenAndThirtyTwoPixels()
    {
        var tia = NewTia();
        Write(tia, Colup0, 0x00);
        Write(tia, Grp0, 0xFF); // solid graphic -> one run the full width
        PositionPlayer0(tia, 30);

        Write(tia, Nusiz0, 0x00);
        var normal = ScanPlayer0(tia);
        Write(tia, Nusiz0, 0x05);
        var doubled = ScanPlayer0(tia);
        Write(tia, Nusiz0, 0x07);
        var quad = ScanPlayer0(tia);

        await Assert.That(LongestRun(normal)).IsEqualTo(8);
        await Assert.That(LongestRun(doubled)).IsEqualTo(16);
        await Assert.That(LongestRun(quad)).IsEqualTo(32);
    }

    [Test]
    [Arguments((byte)0x05)]
    [Arguments((byte)0x07)]
    public async Task StretchModesProduceExactlyOneCopy(byte nusiz)
    {
        var tia = NewTia();
        Write(tia, Nusiz0, nusiz);
        Write(tia, Colup0, 0x00);
        Write(tia, Grp0, 0x80); // one bit; extra copies would show as extra runs
        PositionPlayer0(tia, 30);

        var scan = ScanPlayer0(tia);

        await Assert.That(RunCount(scan)).IsEqualTo(1);
    }

    [Test]
    public async Task ReflectStillMirrorsTheGraphicUnderStretch()
    {
        var tia = NewTia();
        Write(tia, Nusiz0, 0x05);
        Write(tia, Colup0, 0x00);
        Write(tia, Grp0, 0b1110_0000); // top three bits -> leading run of 6 when doubled
        PositionPlayer0(tia, 30);

        Write(tia, Refp0, 0x00);
        var normal = ScanPlayer0(tia);

        Write(tia, Refp0, ReflectD3);
        var reflected = ScanPlayer0(tia);

        // The lit block is the same size either way (three bits x2 = 6), but
        // reflection moves it from the leading edge of the 16-pixel span to the
        // trailing edge - i.e. 10 pixels (five bits x2) later.
        await Assert.That(LongestRun(normal)).IsEqualTo(6);
        await Assert.That(LongestRun(reflected)).IsEqualTo(6);
        await Assert.That(LitCount(normal)).IsEqualTo(6);
        await Assert.That(LitCount(reflected)).IsEqualTo(6);
        await Assert.That(FirstLit(reflected) - FirstLit(normal)).IsEqualTo(10);
    }

    [Test]
    [Arguments((byte)0x01, 2)] // two copies, close
    [Arguments((byte)0x02, 2)] // two copies, medium
    [Arguments((byte)0x03, 3)] // three copies, close
    [Arguments((byte)0x04, 2)] // two copies, wide
    [Arguments((byte)0x06, 3)] // three copies, medium
    public async Task CopyModesAreUnchangedAndDrawOnePixelPerBit(byte nusiz, int expectedCopies)
    {
        var tia = NewTia();
        Write(tia, Nusiz0, nusiz);
        Write(tia, Colup0, 0x00);
        Write(tia, Grp0, 0x80); // one bit per copy
        PositionPlayer0(tia, 20);

        var scan = ScanPlayer0(tia);

        await Assert.That(RunCount(scan)).IsEqualTo(expectedCopies);
        await Assert.That(LongestRun(scan)).IsEqualTo(1);
        await Assert.That(LitCount(scan)).IsEqualTo(expectedCopies);
    }
}
