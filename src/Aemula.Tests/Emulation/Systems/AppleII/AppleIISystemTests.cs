using System;
using System.IO;
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

            if (system.Cpu.Sync && system.Cpu.Address == resetVector)
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
        // should have written some lit pixels into Display.
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

    [Test]
    public async Task RunsAppleIIDeadTestDiagnosticRom()
    {
        // The Apple II Dead Test is a real 2K F8-socket diagnostic ROM (see
        // Assets/README.txt). It runs entirely from ROM registers and its own
        // stack-less code, tests the zero page and stack page, sizes the
        // installed RAM, and only then prints "ZERO/STACK PAGES OK" and starts
        // the main RAM march. Reaching that banner exercises the ROM-override
        // path plus the CPU, address decode, RAM, and text-video pipeline
        // end to end - and the ROM diverges to a red "ZP/SP ERR" screen
        // instead if any of that is wrong, so the banner only appears on a
        // genuine pass.
        var system = new AppleIISystem();
        system.LoadProgram(Path.Combine("Emulation", "Systems", "AppleII", "Assets", "apple2dead.bin"));

        // Apple II text page 1 line bases: line 1 = $0400, line 20 = $05D0,
        // line 23 = $0750. Bytes are stored as screen codes; masking bit 7
        // off recovers ASCII for the character set this ROM uses.
        string ReadTextLine(ushort lineBase)
        {
            var chars = new char[40];
            for (var i = 0; i < chars.Length; i++)
            {
                chars[i] = (char)(system.ReadByteDebug((ushort)(lineBase + i)) & 0x7F);
            }
            return new string(chars);
        }

        var reachedOkBanner = false;
        for (var i = 0; i < 25_000_000 && !reachedOkBanner; i++)
        {
            system.Tick();

            // Only bother scraping the screen periodically - it can't change
            // faster than the CPU can write a whole line.
            if ((i & 0xFFFFF) == 0)
            {
                reachedOkBanner = ReadTextLine(0x05D0).Contains("ZERO/STACK PAGES OK");
            }
        }

        await Assert.That(reachedOkBanner).IsTrue();
        // The RAM sizing found the full 48K ($0200 up to but not including $C000).
        await Assert.That(ReadTextLine(0x0750)).Contains("$0200 TO $BFFF");
        // ...and we're on the pass path, not the "ZP/SP ERR" / "PAGE ERR" screen.
        await Assert.That(ReadTextLine(0x0750)).DoesNotContain("ERR");
    }

    [Test]
    public async Task ShorterRomImageOverlaysTopOfRomSpaceAndRunsFromIt()
    {
        // A 2K image is what a diagnostic like the Apple II Dead Test ships as:
        // it belongs in the F8 socket ($F800-$FFFF), with the lower five
        // sockets left holding the bundled Applesoft image.
        var image = new byte[0x800];
        Array.Fill(image, (byte)0xEA);           // NOP slide from the reset target.
        image[0x400] = 0x42;                     // Marker at $FC00.
        image[0x7FC] = 0x00;                     // Reset vector low  ($FFFC).
        image[0x7FD] = 0xF8;                     // Reset vector high ($FFFD) -> $F800.

        var path = WriteRomToTempFile(image);
        try
        {
            var system = new AppleIISystem();
            system.LoadProgram(path);

            await Assert.That(system.ReadByteDebug(0xFC00)).IsEqualTo((byte)0x42);

            // The lower sockets still hold the bundled ROM.
            var bundled = new AppleIISystem();
            bundled.LoadProgram("");
            await Assert.That(system.ReadByteDebug(0xD000)).IsEqualTo(bundled.ReadByteDebug(0xD000));

            for (var i = 0; i < 5_000; i++)
            {
                system.Tick();
            }

            // The CPU took the overlaid reset vector and is executing the slide.
            await Assert.That(system.Cpu.Address).IsGreaterThanOrEqualTo((ushort)0xF800);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task FullSizeRomImageReplacesEntireRomSpace()
    {
        var image = new byte[0x3000];
        Array.Fill(image, (byte)0xEA);
        image[0x0000] = 0x37;                    // $D000
        image[0x1800] = 0x5A;                    // $E800
        image[0x2FFC] = 0x00;                    // Reset vector low  ($FFFC).
        image[0x2FFD] = 0xD0;                    // Reset vector high ($FFFD) -> $D000.

        var path = WriteRomToTempFile(image);
        try
        {
            var system = new AppleIISystem();
            system.LoadProgram(path);

            await Assert.That(system.ReadByteDebug(0xD000)).IsEqualTo((byte)0x37);
            await Assert.That(system.ReadByteDebug(0xE800)).IsEqualTo((byte)0x5A);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task RejectsRomImageLargerThanRomSpace()
    {
        var path = WriteRomToTempFile(new byte[0x3001]);
        try
        {
            var system = new AppleIISystem();
            await Assert.That(() => system.LoadProgram(path)).ThrowsExactly<InvalidDataException>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteRomToTempFile(byte[] image)
    {
        var path = Path.Combine(Path.GetTempPath(), $"aemula-appleii-rom-{Guid.NewGuid():N}.rom");
        File.WriteAllBytes(path, image);
        return path;
    }
}
