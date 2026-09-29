using Aemula.Emulation.Systems.AppleII;

namespace Aemula.Tests.Emulation.Systems.AppleII;

// One frame of the Apple II's digital VIDEO DATA line, captured directly
// off the system's scanner, one bit per master tick (14 per 7-dot cell)
// for each of the 192 visible lines' 40 cells. Lets tests assert on what
// the hardware actually puts on the video signal without going through the
// composite encoder and Television decoder.
internal sealed class AppleIIVideoFrame
{
    public const int Lines = 192;
    public const int Cells = 40;
    public const int TicksPerCell = 14;

    private readonly bool[] _bits = new bool[Lines * Cells * TicksPerCell];

    // The video data bit for the given tick (0-13) of the given cell
    // (0-39) on the given visible line (0-191).
    public bool this[int line, int cell, int tick] => _bits[(line * Cells + cell) * TicksPerCell + tick];

    // A HIRES/TEXT dot's bit: both of a dot's two master ticks carry it.
    public bool Dot(int line, int cell, int dot) => this[line, cell, dot * 2];

    public bool AnyLit(int firstLine, int lastLine)
    {
        for (var line = firstLine; line <= lastLine; line++)
        {
            for (var cell = 0; cell < Cells; cell++)
            {
                for (var tick = 0; tick < TicksPerCell; tick++)
                {
                    if (this[line, cell, tick])
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    public bool AllLit(int firstLine, int lastLine)
    {
        for (var line = firstLine; line <= lastLine; line++)
        {
            for (var cell = 0; cell < Cells; cell++)
            {
                for (var tick = 0; tick < TicksPerCell; tick++)
                {
                    if (!this[line, cell, tick])
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    // The 14 bits of one cell as a string of '0'/'1', tick 0 first.
    public string CellBits(int line, int cell)
    {
        var chars = new char[TicksPerCell];

        for (var tick = 0; tick < TicksPerCell; tick++)
        {
            chars[tick] = this[line, cell, tick] ? '1' : '0';
        }

        return new string(chars);
    }

    // Ticks the system for at least one full frame's worth of scanning and
    // returns the last complete pass over the visible picture.
    public static AppleIIVideoFrame Capture(AppleIISystem system)
    {
        // Two frames' worth of ticks; the second pass simply overwrites
        // the first, so the result is always a full frame regardless of
        // where in the frame the capture started.
        const int TicksToCapture = 2 * 262 * 65 * 14 + 100_000;

        var frame = new AppleIIVideoFrame();
        var wasPhase0 = system.Phase0;
        var line = -1;
        var previousCell = int.MaxValue;

        for (var i = 0; i < TicksToCapture; i++)
        {
            system.Tick();

            var isPhase0 = system.Phase0;
            var risingEdge = isPhase0 && !wasPhase0;
            wasPhase0 = isPhase0;

            if (!risingEdge)
            {
                continue;
            }

            if (system.Vbl)
            {
                line = -1;
                previousCell = int.MaxValue;
                continue;
            }

            if (system.Hbl)
            {
                continue;
            }

            // H0-H5 (masking off HPE'); the first visible cell is 24.
            var (h, _) = system.GetVideoScannerStateForTests();
            var cell = (h & 0b0_111111) - 24;

            if (cell < previousCell)
            {
                line++;
            }

            previousCell = cell;

            if ((uint)line >= Lines || (uint)cell >= Cells)
            {
                continue;
            }

            var bits = system.GetVideoDataBitsForTests();

            for (var tick = 0; tick < TicksPerCell; tick++)
            {
                frame._bits[(line * Cells + cell) * TicksPerCell + tick] = bits[tick];
            }
        }

        return frame;
    }
}
