using System.Threading.Tasks;
using Aemula.Emulation.Chips.Saa5050;

namespace Aemula.Tests.Emulation.Chips.Saa5050;

public class Saa5050ChipTests
{
    // Shows one scan line of cells, as the 12 half-dots of each as colours
    // (R=1, G=2, B=4). A cell is shown two character clocks after its code is
    // taken, and LOSE follows each code a clock later, as the BBC Micro's latch
    // gives it; the two clocks after the last code show the last cells and,
    // with LOSE low, end the line.
    private static int[][] Line(Saa5050Chip chip, params byte[] codes)
    {
        var tr6 = false;
        var cells = new int[codes.Length][];

        for (var clock = 0; clock < codes.Length + 2; clock++)
        {
            chip.Data = clock < codes.Length ? codes[clock] : (byte)' ';
            chip.F1 = true;
            chip.F1 = false;
            chip.Lose = clock < codes.Length;

            if (clock < 2)
            {
                continue;
            }

            var cell = cells[clock - 2] = new int[12];

            for (var dot = 0; dot < 12; dot++)
            {
                if (dot > 0)
                {
                    chip.Tr6 = !tr6;
                    tr6 = !tr6;
                }

                cell[dot] = (chip.Red ? 1 : 0) | (chip.Green ? 2 : 0) | (chip.Blue ? 4 : 0);
            }

            // The twelfth edge, which leaves TR6 where it started for the
            // next line.
            chip.Tr6 = !tr6;
            tr6 = !tr6;
        }

        return cells;
    }

    // A chip at the start of a field, showing the given line (0-19) of the row.
    private static Saa5050Chip AtLine(int line)
    {
        var chip = new Saa5050Chip();
        chip.Dew = true;
        chip.Dew = false;
        chip.Crs = (line & 1) == 0;

        for (var i = 0; i < line >> 1; i++)
        {
            Line(chip, (byte)' ');
        }

        return chip;
    }

    private static bool AllDots(int[] cell, int colour)
    {
        foreach (var dot in cell)
        {
            if (dot != colour)
            {
                return false;
            }
        }

        return true;
    }

    [Test]
    public async Task TextIsWhiteOnBlackAtTheStartOfALine()
    {
        // The vertical bar of "!" is the dot in the fourth column.
        var cells = Line(AtLine(2), (byte)'!');
        await Assert.That(cells[0][6]).IsEqualTo(7);
        await Assert.That(cells[0][0]).IsEqualTo(0);
    }

    [Test]
    public async Task AColourCodeColoursTheCellsAfterIt()
    {
        var cells = Line(AtLine(2), 1, (byte)'!', 2, (byte)'!');
        await Assert.That(AllDots(cells[0], 0)).IsTrue();
        await Assert.That(cells[1][6]).IsEqualTo(1);
        await Assert.That(cells[3][6]).IsEqualTo(2);
    }

    [Test]
    public async Task NewBackgroundTakesTheForegroundColourFromItsOwnCell()
    {
        var cells = Line(AtLine(2), 4, 29, (byte)' ');
        await Assert.That(AllDots(cells[1], 4)).IsTrue();
        await Assert.That(AllDots(cells[2], 4)).IsTrue();
    }

    [Test]
    public async Task LineEndReturnsToWhiteOnBlack()
    {
        var chip = AtLine(2);
        Line(chip, 1, 29);
        var cells = Line(chip, (byte)'!');
        await Assert.That(cells[0][6]).IsEqualTo(7);
        await Assert.That(cells[0][0]).IsEqualTo(0);
    }

    [Test]
    public async Task ConcealHidesTheRestOfTheLineUntilAColourCode()
    {
        var cells = Line(AtLine(2), 24, (byte)'!', 3, (byte)'!');
        await Assert.That(AllDots(cells[1], 0)).IsTrue();
        await Assert.That(cells[3][6]).IsEqualTo(3);
    }

    [Test]
    public async Task ContiguousMosaicBlocksFillTheirCells()
    {
        // Code &7F lights all six blocks.
        var cells = Line(AtLine(2), 0x17, 0x7F);
        await Assert.That(AllDots(cells[1], 7)).IsTrue();
    }

    [Test]
    public async Task SeparatedMosaicBlocksLeaveAGapAtTheLeft()
    {
        var cells = Line(AtLine(2), 0x17, 26, 0x7F);

        // Each block loses its leftmost dot (two half-dots) and the
        // right-hand block, which starts at dot 3, has a gap before it too.
        await Assert.That(cells[2][0]).IsEqualTo(0);
        await Assert.That(cells[2][1]).IsEqualTo(0);
        await Assert.That(cells[2][2]).IsEqualTo(7);
        await Assert.That(cells[2][6]).IsEqualTo(0);
        await Assert.That(cells[2][8]).IsEqualTo(7);
    }

    [Test]
    public async Task HeldGraphicsRepeatsTheLastMosaicOverControlCodes()
    {
        // hold graphics (30), then a colour code, whose cell shows the held
        // mosaic rather than a space.
        var cells = Line(AtLine(2), 0x17, 0x7F, 30, 0x16, 0x20);
        await Assert.That(AllDots(cells[2], 7)).IsTrue();
        await Assert.That(AllDots(cells[3], 7)).IsTrue();

        // The hold applies to the colour change that came after it: the
        // space is cyan on black.
        await Assert.That(AllDots(cells[4], 0)).IsTrue();
    }

    [Test]
    public async Task DoubleHeightRepeatsTheTopHalfOnTheNextRow()
    {
        // Row 1: "13, !" draws the top half; the next row draws the bottom
        // half of the same cell, so line 10 of the second row looks like
        // line 0 of the first doubled.
        var chip = AtLine(0);

        for (var scan = 0; scan < 10; scan++)
        {
            // Skip to the second row of the page by running the scan lines.
            Line(chip, 13, (byte)'!');
        }

        var secondRow = Line(chip, 13, (byte)'!');
        var firstHalf = Line(AtLine(0), 13, (byte)'!');

        // The bottom half begins with the part of the glyph that was the
        // sixth line of the cell, which is the dot of "!" in its sixth line
        // (the stem ends after the fifth).
        await Assert.That(firstHalf[1][6]).IsEqualTo(0);
        await Assert.That(secondRow[1][6]).IsEqualTo(7);
    }

    [Test]
    public async Task FlashHidesTextWhileTheFlashPhaseIsOff()
    {
        var chip = new Saa5050Chip();
        chip.Crs = true;

        // Flash is hidden for the first 16 of every 64 fields.
        chip.Dew = true;
        chip.Dew = false;
        Line(chip, (byte)' ');
        Line(chip, (byte)' ');
        var hidden = Line(chip, 8, (byte)'!');
        await Assert.That(hidden[1][6]).IsEqualTo(0);

        for (var i = 0; i < 16; i++)
        {
            chip.Dew = true;
            chip.Dew = false;
        }

        Line(chip, (byte)' ');
        Line(chip, (byte)' ');
        var shown = Line(chip, 8, (byte)'!');
        await Assert.That(shown[1][6]).IsEqualTo(7);
    }

    [Test]
    public async Task BlankingInputForcesTheOutputsLow()
    {
        var chip = AtLine(2);
        chip.Blan = false;
        var cells = Line(chip, (byte)'!');
        await Assert.That(AllDots(cells[0], 0)).IsTrue();
    }
}
