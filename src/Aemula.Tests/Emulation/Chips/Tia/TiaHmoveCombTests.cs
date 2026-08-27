using System.Threading.Tasks;
using Aemula.Emulation.Chips.Tia;

namespace Aemula.Tests.Emulation.Chips.Tia;

// The HMOVE comb: strobing HMOVE while the beam is in horizontal blank holds
// the blank on for an extra 8 colour clocks into the line, so the leftmost 8
// visible pixels come out border-black. Driven through the full colour-clock
// machinery via Osc, sampling TiaChip.Blk/Col per clock, because this is
// entirely about horizontal timing.
public class TiaHmoveCombTests
{
    private const byte Colubk = 0x09;
    private const byte Hmove = 0x2A;
    private const byte Hmclr = 0x2B;

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

    private static void Tick(TiaChip tia)
    {
        tia.Osc = false;
        tia.Osc = true;
    }

    // Advance to the first colour clock of a horizontal-blank stretch, after
    // warming the horizontal counter up over a few lines.
    private static void ToBlankStart(TiaChip tia)
    {
        for (var i = 0; i < 3; i++)
        {
            while (tia.Blk)
            {
                Tick(tia);
            }

            while (!tia.Blk)
            {
                Tick(tia);
            }
        }

        while (tia.Blk)
        {
            Tick(tia);
        }

        while (!tia.Blk)
        {
            Tick(tia);
        }
    }

    // Index of the first non-blank colour clock, counting from a blank-start
    // boundary, optionally strobing HMOVE a few clocks in (still inside the
    // blank). Also captures Col across the run so the comb clocks can be
    // checked for black.
    private static (int FirstVisible, byte[] Col) MeasureBlankRun(TiaChip tia, bool strobeHmove)
    {
        var col = new byte[256];
        var firstVisible = -1;

        for (var i = 0; i < col.Length; i++)
        {
            if (!tia.Blk && firstVisible < 0)
            {
                firstVisible = i;
                break;
            }

            col[i] = tia.Col;

            if (strobeHmove && i == 5)
            {
                Write(tia, Hmove, 0);
            }

            Tick(tia);
        }

        return (firstVisible, col);
    }

    [Test]
    public async Task InBlankHmoveHoldsBlankFor8ExtraColourClocks()
    {
        var normalTia = NewTia();
        Write(normalTia, Colubk, 0x3C); // a lit background, so "black" is unambiguous
        Write(normalTia, Hmclr, 0);
        ToBlankStart(normalTia);
        var (firstVisibleNormal, _) = MeasureBlankRun(normalTia, strobeHmove: false);

        var hmoveTia = NewTia();
        Write(hmoveTia, Colubk, 0x3C);
        Write(hmoveTia, Hmclr, 0);
        ToBlankStart(hmoveTia);
        var (firstVisibleHmove, hmoveCol) = MeasureBlankRun(hmoveTia, strobeHmove: true);

        await Assert.That(firstVisibleNormal).IsGreaterThan(0);

        // The HMOVE line's visible region starts exactly 8 colour clocks
        // later than the untouched line's.
        await Assert.That(firstVisibleHmove - firstVisibleNormal).IsEqualTo(8);

        // Those 8 extra clocks are genuine border-black: still blanked, and
        // Col forced to 0 (no colour burst this late in the blank).
        for (var i = firstVisibleNormal; i < firstVisibleHmove; i++)
        {
            await Assert.That(hmoveCol[i]).IsEqualTo((byte)0);
        }
    }

    [Test]
    public async Task NoCombOnALineWithoutHmove()
    {
        var tia = NewTia();
        Write(tia, Hmclr, 0);

        ToBlankStart(tia);
        var (firstA, _) = MeasureBlankRun(tia, strobeHmove: false);

        ToBlankStart(tia);
        var (firstB, _) = MeasureBlankRun(tia, strobeHmove: false);

        // Two consecutive HMOVE-free lines have identical blank widths - no
        // 8-clock comb creeps in on its own.
        await Assert.That(firstA).IsEqualTo(firstB);
    }

    [Test]
    public async Task CombIsConfinedToTheHmoveLine()
    {
        var tia = NewTia();
        Write(tia, Hmclr, 0);

        ToBlankStart(tia);
        var (combedLine, _) = MeasureBlankRun(tia, strobeHmove: true);

        // The line after the HMOVE strobe is back to normal - _hmove is
        // cleared at the RESET state each line, so the comb does not repeat.
        ToBlankStart(tia);
        var (nextLine, _) = MeasureBlankRun(tia, strobeHmove: false);

        ToBlankStart(tia);
        var (cleanLine, _) = MeasureBlankRun(tia, strobeHmove: false);

        await Assert.That(combedLine - nextLine).IsEqualTo(8);
        await Assert.That(nextLine).IsEqualTo(cleanLine);
    }
}
