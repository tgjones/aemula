using System.Collections.Generic;
using System.Threading.Tasks;
using Aemula.Emulation.Output;
using Aemula.Emulation.Systems.AppleI;

namespace Aemula.Tests.Emulation.Systems.AppleI;

// The WozMon boot screen as the Television sees it: the composite signal
// AppleISystem.CompositeVideo.cs produces, decoded by Television, has to
// come out as a stable raster with the "\" prompt at the top-left cell and
// the cursor blinking on the row below - the thing a person checks by eye
// in the UI, pinned down sample-for-sample.
public class AppleISystemTelevisionTests
{
    // 65 character-times of 14 master ticks per line, 262 lines per frame
    // (see AppleISystemVideoTimingTests).
    private const int MasterTicksPerLine = 65 * 14;
    private const int LinesPerFrame = 262;
    private const int MasterTicksPerFrame = MasterTicksPerLine * LinesPerFrame;

    // Vertical sync ends as the line counter reaches 232, and the
    // Television numbers its rows from that trailing edge, so lines 232-255
    // are rows 0-23 and line 0 is row 24.
    private const int RowOfLineZero = 24;

    // Text row r is loaded into the line buffer during line 8r+7 and shown
    // on lines 8r+8 to 8r+15 (AppleISystem.CharacterMemory.cs), so its
    // glyph row g is on line 8 + 8r + g.
    private static int RowOf(int textRow, int glyphRow) => RowOfLineZero + 8 + 8 * textRow + glyphRow;

    // Column 0 is the first sample after HSYNC's trailing edge, one master
    // tick into character-time 110 (ICC13's FF2 re-times sync by a tick).
    // A cell's seven dots start on the last dot of its character-time
    // (the 74166 loads on the sixth dot and shows the new H on the seventh)
    // - character-time 120 for column 0 - so column j's first dot is sample
    // 10 * 14 - 1 + 12 + 14j, two samples per dot.
    private const int FirstDotColumn = 151;
    private const int SamplesPerCell = 14;

    private static int ColumnOfDot(int textColumn, int dot) => FirstDotColumn + SamplesPerCell * textColumn + 2 * dot;

    private static ulong RunFrame(AppleISystem system)
    {
        var television = system.Television;
        var previousRow = television.CurrentRow;
        var ticks = 0UL;

        while (true)
        {
            system.Tick();
            ticks++;

            var row = television.CurrentRow;
            if (row < previousRow)
            {
                return ticks;
            }
            previousRow = row;
        }
    }

    private static HashSet<(int Row, int Column)> WhiteSamples(Television television)
    {
        var width = (int)television.SampleBuffer.Width;
        var height = (int)television.SampleBuffer.Height;
        var samples = television.SampleBuffer.Data;
        var white = new HashSet<(int, int)>();

        for (var row = 0; row < height; row++)
        {
            for (var column = 0; column < width; column++)
            {
                if (samples[row * width + column].RawSample > 128)
                {
                    white.Add((row, column));
                }
            }
        }

        return white;
    }

    [Test]
    public async Task WozMonBootScreenShowsThePromptTopLeftAndTheCursorBlinkingBelowIt()
    {
        var system = new AppleISystem();
        system.LoadProgram("");
        var television = system.Television;

        // Read the raw composite level rather than the decoded colour: the
        // YIQ decoder's filtering smears a two-sample dot's edges, and this
        // is checking the signal the board produced, sample for sample.
        television.CaptureSampleDiagnostics = true;

        // Past reset, the "\" + CR echo, and the decoder's own settling.
        for (var i = 0; i < 30; i++)
        {
            RunFrame(system);
        }

        await Assert.That(television.DetectedSamplesPerLine).IsEqualTo(910f).Within(0.01f);
        await Assert.That(television.DetectedLinesPerFrame).IsEqualTo((float)LinesPerFrame).Within(0.01f);

        // The back porch is the same ten character-times as the sync pulse,
        // so self-calibrated active video starts exactly where the 40-column
        // window does.
        await Assert.That(television.ActiveVideoStartSamples).IsEqualTo(140f).Within(0.5f);

        // The 2513's "\" glyph is a diagonal on its rows 2-6, one dot per
        // row from the left; the "@" the cursor shows fills rows 1-7.
        var backslash = new HashSet<(int, int)>();
        for (var glyphRow = 2; glyphRow <= 6; glyphRow++)
        {
            var column = ColumnOfDot(0, glyphRow - 2);
            backslash.Add((RowOf(0, glyphRow), column));
            backslash.Add((RowOf(0, glyphRow), column + 1));
        }

        var framesWithCursor = 0;
        var framesWithoutCursor = 0;

        for (var frame = 0; frame < 40; frame++)
        {
            await Assert.That(RunFrame(system)).IsEqualTo((ulong)MasterTicksPerFrame);

            var white = WhiteSamples(television);

            foreach (var sample in backslash)
            {
                await Assert.That(white.Contains(sample)).IsTrue();
            }

            var cursorSamples = 0;
            foreach (var (row, column) in white)
            {
                if (backslash.Contains((row, column)))
                {
                    continue;
                }

                // Anything else lit must be inside the cursor cell.
                await Assert.That(row).IsBetween(RowOf(1, 1), RowOf(1, 7));
                await Assert.That(column).IsBetween(ColumnOfDot(1, 0) - SamplesPerCell, ColumnOfDot(1, 0) - 1);
                cursorSamples++;
            }

            if (cursorSamples > 0)
            {
                framesWithCursor++;
            }
            else
            {
                framesWithoutCursor++;
            }
        }

        // The 555 (ICD13) flashes it at about 2Hz, so 40 frames sees both.
        await Assert.That(framesWithCursor).IsGreaterThan(0);
        await Assert.That(framesWithoutCursor).IsGreaterThan(0);
    }
}
