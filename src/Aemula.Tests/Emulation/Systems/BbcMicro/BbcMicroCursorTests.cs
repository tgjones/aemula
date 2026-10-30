using System;
using System.Threading.Tasks;
using Aemula.Emulation.Systems.BbcMicro;

namespace Aemula.Tests.Emulation.Systems.BbcMicro;

// The BASIC prompt's cursor is the character cell straight after the ">", in
// every screen mode. The 6845, the video ULA and, in Mode 7, the SAA5050 each
// add their own delay between the 6845 addressing a character and its dots
// reaching the screen, and the cursor only lands on its cell when they agree.
public class BbcMicroCursorTests
{
    private static void Run(BbcMicroSystem system, int ticks)
    {
        for (var tick = 0; tick < ticks; tick++)
        {
            system.Tick();
        }
    }

    private static void Type(BbcMicroSystem system, string text)
    {
        foreach (var character in text)
        {
            system.OnKeyEvent(new KeyEvent { IsDown = true, Key = (Key)character, Character = character });
            Run(system, 1_600_000);
            system.OnKeyEvent(new KeyEvent { IsDown = false, Key = (Key)character, Character = character });
            Run(system, 1_600_000);
        }
    }

    // Mode, the width of a character cell in 16MHz ticks, and the first
    // scanline of the cursor, below every line the ">" lights.
    [Test]
    [Arguments(0, 8, 7)]
    [Arguments(1, 16, 7)]
    [Arguments(2, 32, 7)]
    [Arguments(3, 8, 7)]
    [Arguments(4, 16, 7)]
    [Arguments(5, 32, 7)]
    [Arguments(6, 16, 7)]
    [Arguments(7, 16, 18)]
    public async Task PromptCursorSitsInTheCellAfterTheGreaterThanSign(int mode, int cellTicks, int cursorLine)
    {
        var system = new BbcMicroSystem(null, 100);
        Run(system, 8_000_000);

        if (mode != 7)
        {
            Type(system, $"MODE {mode}\r");
            Run(system, 2_000_000);
        }

        // Ticks after the start of HSYNC at which the prompt row lights the
        // ">" and the cursor. The row is found from the 6845's cursor output,
        // and runs until the cursor has been seen for two fields (the cursor
        // blinks, so it may be some fields before it shows).
        var sinceHsync = 0;
        var hsync = false;
        var cursorMa = -1;
        var ticksAfterCursor = -1;
        int glyphMin = int.MaxValue, glyphMax = -1, cursorMin = int.MaxValue, cursorMax = -1;

        for (var tick = 0; tick < 12_000_000 && ticksAfterCursor < 640_000; tick++)
        {
            system.Tick();

            if (ticksAfterCursor >= 0)
            {
                ticksAfterCursor++;
            }

            var hsyncNow = system.Crtc.HSync;
            sinceHsync = hsyncNow && !hsync ? 0 : sinceHsync + 1;
            hsync = hsyncNow;

            if (cursorMa < 0 && system.Crtc.Cursor)
            {
                cursorMa = system.Crtc.MA;
            }

            if (!system.VideoUla.Red || cursorMa < 0 || Math.Abs(system.Crtc.MA - cursorMa) > 8)
            {
                continue;
            }

            if (system.Crtc.RA >= cursorLine)
            {
                cursorMin = Math.Min(cursorMin, sinceHsync);
                cursorMax = Math.Max(cursorMax, sinceHsync);
                ticksAfterCursor = Math.Max(ticksAfterCursor, 0);
            }
            else
            {
                glyphMin = Math.Min(glyphMin, sinceHsync);
                glyphMax = Math.Max(glyphMax, sinceHsync);
            }
        }

        // The cursor fills one cell, and the glyph lies wholly in the cell
        // before it.
        await Assert.That(cursorMax - cursorMin).IsEqualTo(cellTicks - 1);
        await Assert.That(glyphMin).IsGreaterThanOrEqualTo(cursorMin - cellTicks);
        await Assert.That(glyphMax).IsLessThan(cursorMin);
    }
}
