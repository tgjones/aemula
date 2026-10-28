using System;
using System.Threading.Tasks;
using Aemula.Emulation.Systems.BbcMicro;

namespace Aemula.Tests.Emulation.Systems.BbcMicro;

// Mode 7's screen memory reaches the SAA5050 through the latch IC15, and the
// chip's colour outputs replace the video ULA's pixels.
public class BbcMicroTeletextTests
{
    private const uint ShortResetPulseTicks = 100;

    private static BbcMicroSystem BootToPrompt()
    {
        var system = new BbcMicroSystem(null, ShortResetPulseTicks);

        for (var tick = 0; tick < 12_000_000; tick++)
        {
            system.Tick();

            if (tick % 100_000 == 0 && ScreenHasPrompt(system))
            {
                return system;
            }
        }

        throw new InvalidOperationException("The BASIC prompt never appeared.");
    }

    private static bool ScreenHasPrompt(BbcMicroSystem system)
    {
        for (var offset = 0; offset < 40 * 25; offset++)
        {
            if (system.ReadByteDebug((ushort)(0x7C00 + offset)) == (byte)'>')
            {
                return true;
            }
        }

        return false;
    }

    // The number of scan lines in two PAL frames (interlaced fields of 312.5 lines of 64us) in which
    // the video ULA's output is lit for 600 or more consecutive 16MHz ticks, longer than a cursor
    // or a letter can light it but shorter than 39 solid characters (624 ticks).
    private static int SolidLines(BbcMicroSystem system)
    {
        var lines = 0;
        var run = 0;

        for (var tick = 0; tick < 1_280_000; tick++)
        {
            system.Tick();

            if (system.VideoUla.Red || system.VideoUla.Green || system.VideoUla.Blue)
            {
                if (++run == 600)
                {
                    lines++;
                }
            }
            else
            {
                run = 0;
            }
        }

        return lines;
    }

    private static void FillScreen(BbcMicroSystem system, byte first, byte rest, int cells)
    {
        for (var offset = 0; offset < 1000; offset++)
        {
            system.WriteByteDebug((ushort)(0x7C00 + offset), 0x20);
        }

        system.WriteByteDebug(0x7C00, first);

        for (var offset = 1; offset < cells; offset++)
        {
            system.WriteByteDebug((ushort)(0x7C00 + offset), rest);
        }
    }

    [Test]
    public async Task ScreenCharactersLightTheOutput()
    {
        var system = BootToPrompt();

        FillScreen(system, 0x20, 0x20, 1);
        var blank = SolidLines(system);

        // Graphics white, then 39 cells of solid mosaic: every scan line of the row is lit for the
        // 39 characters of 16 ticks each. The row is drawn for 10 scan lines in each field.
        FillScreen(system, 0x97, 0x7F, 40);
        var row = SolidLines(system);

        await Assert.That(blank).IsEqualTo(0);
        await Assert.That(row).IsBetween(36, 44);
    }
}
