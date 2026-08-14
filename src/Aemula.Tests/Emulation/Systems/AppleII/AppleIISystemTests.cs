using System.Threading.Tasks;
using Aemula.Emulation.Systems.AppleII;
using Hexa.NET.SDL3;

namespace Aemula.Tests.Emulation.Systems.AppleII;

public class AppleIISystemTests
{
    [Test]
    public async Task RunsResetVectorFromRom()
    {
        var system = new AppleIISystem();
        system.LoadProgram("");

        // The Autostart ROM's reset vector, read straight from Apple2_Plus.rom.
        const ushort resetVector = 0xFA62;

        var maxCycles = 100_000;
        var cycles = 0;
        var reachedResetVector = false;

        while (cycles < maxCycles)
        {
            system.Tick();

            if (system.Cpu.PC == resetVector)
            {
                reachedResetVector = true;
                break;
            }

            cycles++;
        }

        await Assert.That(reachedResetVector).IsTrue();
    }

    [Test]
    public async Task BootBannerRendersLitPixels()
    {
        // The Autostart ROM prints an "APPLE ][" banner and BASIC prompt
        // without any input - after enough emulated time to both run that
        // code and scan a few frames of video, the text-mode pipeline
        // (phase 4) should have written some lit pixels into Display.
        var system = new AppleIISystem();
        system.LoadProgram("");

        for (var i = 0; i < 2_000_000; i++)
        {
            system.Tick();
        }

        var sawLitPixel = false;
        foreach (var pixel in system.Display.Data)
        {
            if (pixel.R != 0)
            {
                sawLitPixel = true;
                break;
            }
        }

        await Assert.That(sawLitPixel).IsTrue();
    }

    [Test]
    public async Task KeyPressReachesKeyboardLatch()
    {
        var system = new AppleIISystem();
        system.LoadProgram("");

        for (var i = 0; i < 500_000; i++)
        {
            system.Tick();
        }

        system.OnKeyEvent(new SDLKeyboardEvent { Type = SDLEventType.KeyDown, Key = (int)'a' });

        for (var i = 0; i < 200_000; i++)
        {
            system.Tick();
        }

        // Sather's Table 7.2: "A" alone reads as $C1 at $C000 (bit 7 is the
        // strobe flag, bits 0-6 are the uppercase ASCII code).
        await Assert.That(system.ReadByteDebug(0xC000)).IsEqualTo((byte)0xC1);

        system.OnKeyEvent(new SDLKeyboardEvent { Type = SDLEventType.KeyUp, Key = (int)'a' });

        // Reading $C010 clears the strobe; the data bits (a stale, latched
        // "A") stay put until another key is pressed.
        system.ReadByteDebug(0xC010);

        await Assert.That(system.ReadByteDebug(0xC000)).IsEqualTo((byte)0x41);
    }
}
