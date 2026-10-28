using System.Threading.Tasks;
using Aemula.Emulation.Chips.VideoUla;

namespace Aemula.Tests.Emulation.Chips.VideoUla;

public class VideoUlaChipTests
{
    private static void Run(VideoUlaChip ula, int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            ula.Tick();
        }
    }

    // A chip showing solid white (palette entry 15 is logical colour 7, whose
    // inverted bits are 0) for the video byte &FF, 1MHz characters, 16MHz pixels.
    private static VideoUlaChip White(byte control)
    {
        var ula = new VideoUlaChip();
        ula.Write(false, control);
        ula.Write(true, 0xF0);
        ula.Data = 0xFF;
        ula.Disen = true;
        return ula;
    }

    [Test]
    public async Task DisplayEnableBlanksTheOutputOneTickLate()
    {
        var ula = White(0x0C);
        Run(ula, 2);
        await Assert.That(ula.Red && ula.Green && ula.Blue).IsTrue();

        ula.Disen = false;
        ula.Tick();
        await Assert.That(ula.Red).IsTrue();
        ula.Tick();
        await Assert.That(ula.Red || ula.Green || ula.Blue).IsFalse();
    }

    // Counts the fetches of a black byte, each of which breaks a run of
    // white (the ones the shifter fills itself with) over 32 clocks.
    private static int CountFetches(byte control)
    {
        var ula = new VideoUlaChip();
        ula.Write(false, control);
        ula.Write(true, 0x07);
        ula.Write(true, 0xF0);
        ula.Data = 0x00;
        ula.Disen = true;
        Run(ula, 16);

        var fetches = 0;
        var wasWhite = true;

        for (var i = 0; i < 32; i++)
        {
            ula.Tick();
            var white = ula.Red && ula.Green && ula.Blue;
            fetches += wasWhite && !white ? 1 : 0;
            wasWhite = white;
        }

        return fetches;
    }

    [Test]
    public async Task FetchesAtCountZeroAndAlsoCountEightWithTheFastCharacterClock()
    {
        await Assert.That(CountFetches(0x0C)).IsEqualTo(2);
        await Assert.That(CountFetches(0x1C)).IsEqualTo(4);
    }

    // The cursor inverts one character for the first segment bit, the next
    // for the second, and the following two share the third.
    [Test]
    [Arguments(0x80, new[] { true, false, false, false, false })]
    [Arguments(0x40, new[] { false, true, false, false, false })]
    [Arguments(0x20, new[] { false, false, true, true, false })]
    [Arguments(0xE0, new[] { true, true, true, true, false })]
    public async Task CursorInvertsTheCharactersItsSegmentBitsSelect(int segments, bool[] inverted)
    {
        // Black screen, so an inverted character is white.
        var ula = new VideoUlaChip();
        ula.Write(false, (byte)(segments | 0x0C));
        ula.Write(true, 0x07);
        ula.Data = 0;
        ula.Disen = true;

        for (var character = 0; character < inverted.Length; character++)
        {
            // The 6845 flags the cursor character as it is fetched.
            ula.Cursor = character == 0;
            ula.Tick();
            ula.Tick();
            await Assert.That(ula.Red).IsEqualTo(inverted[character]);
            Run(ula, 14);
        }
    }

    [Test]
    public async Task TeletextModePassesTheTeletextColoursThroughWithTheCursor()
    {
        var ula = new VideoUlaChip();
        ula.Write(false, 0x82);
        ula.RedInput = true;
        ula.Tick();
        await Assert.That(ula.Red).IsTrue();
        await Assert.That(ula.Green).IsFalse();

        // The next fetch, at count 0, picks the cursor up.
        ula.Cursor = true;
        Run(ula, 17);
        ula.Cursor = false;
        await Assert.That(ula.Red).IsFalse();
        await Assert.That(ula.Green).IsTrue();
    }
}
