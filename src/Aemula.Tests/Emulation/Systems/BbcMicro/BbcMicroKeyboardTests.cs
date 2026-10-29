using System;
using System.Text;
using System.Threading.Tasks;
using Aemula.Emulation.Systems.BbcMicro;

namespace Aemula.Tests.Emulation.Systems.BbcMicro;

// Typing at the BASIC prompt exercises the whole path: matrix, port A / CA2
// wiring, the addressable latch's keyboard-enable bit and the OS's scan.
public class BbcMicroKeyboardTests
{
    private const uint ShortResetPulseTicks = 100;
    private const int ScreenAddress = 0x7C00;

    private static void Run(BbcMicroSystem system, int ticks)
    {
        for (var tick = 0; tick < ticks; tick++)
        {
            system.Tick();
        }
    }

    private static BbcMicroSystem BootToPrompt()
    {
        var system = new BbcMicroSystem(null, ShortResetPulseTicks);

        for (var tick = 0; tick < 12_000_000; tick++)
        {
            system.Tick();

            if (tick % 100_000 == 0 && ScreenText(system).Contains('>'))
            {
                return system;
            }
        }

        throw new InvalidOperationException("The BASIC prompt never appeared.");
    }

    private static string ScreenText(BbcMicroSystem system)
    {
        var text = new StringBuilder();

        for (var offset = 0; offset < 40 * 25; offset++)
        {
            var value = system.ReadByteDebug((ushort)(ScreenAddress + offset)) & 0x7F;
            text.Append(value is >= 0x20 and < 0x7F ? (char)value : ' ');
        }

        return text.ToString();
    }

    private static void Type(BbcMicroSystem system, string text)
    {
        foreach (var character in text)
        {
            // Two 50Hz frames down and two up is a keypress the OS's
            // keyboard interrupt cannot miss.
            var key = new KeyEvent { IsDown = true, Key = (Key)character, Character = character };
            system.OnKeyEvent(key);
            Run(system, 1_600_000);
            system.OnKeyEvent(new KeyEvent { IsDown = false, Key = (Key)character, Character = character });
            Run(system, 1_600_000);
        }
    }

    [Test]
    public async Task Typed_text_appears_at_the_prompt()
    {
        var system = BootToPrompt();

        Type(system, "PRINT \"HI\"\r");
        Run(system, 3_200_000);

        await Assert.That(ScreenText(system)).Contains("PRINT \"HI\"");
        await Assert.That(ScreenText(system)).Contains("HI  ");
    }

    [Test]
    public async Task Shifted_symbols_use_the_BBC_shift_legends()
    {
        var system = BootToPrompt();

        Type(system, "A=1+2*3\r");
        Type(system, "PRINT A\r");
        Run(system, 3_200_000);

        await Assert.That(ScreenText(system)).Contains("A=1+2*3");
        await Assert.That(ScreenText(system)).Contains("7");
    }
}
