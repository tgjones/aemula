using System.Collections.Generic;
using System.Threading.Tasks;
using Aemula.Emulation.Output;
using Aemula.Emulation.Systems.ZX80;

namespace Aemula.Tests.Emulation.Systems;

// The composite path (ZX80System.CompositeVideo.cs) plus a live keypress,
// as far as this phase can check it without exact frame timing (that's
// phase 6's job, once the NTSC strap diode picks a definite line/frame
// count) - does Television lock onto a stable signal at all, and does a
// keypress actually show up in what it decodes.
public class ZX80SystemTelevisionTests
{
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
    public async Task LocksOntoAStableLineLengthAfterBooting()
    {
        var system = new ZX80System();
        var television = system.Television;

        // Past reset and into the ROM's idle keyboard-scan loop at the "K"
        // cursor - long enough for the sync separator's PLL to settle on a
        // real, repeating HSYNC period rather than still-transient startup
        // noise.
        for (var i = 0; i < 30_000_000; i++)
        {
            system.Tick();
        }

        await Assert.That(television.DetectedSamplesPerLine).IsGreaterThan(0f);
        await Assert.That(television.DetectedLinesPerFrame).IsGreaterThan(0f);
    }

    [Test]
    public async Task HoldingDownAKeyChangesWhatsOnScreen()
    {
        var system = new ZX80System();
        var television = system.Television;
        television.CaptureSampleDiagnostics = true;

        for (var i = 0; i < 30_000_000; i++)
        {
            system.Tick();
        }

        var baseline = WhiteSamples(television);

        system.OnKeyEvent(new KeyEvent { IsDown = true, Key = Key.A, Character = 'a' });

        // A few frames' worth of ticks: enough for the ROM's own keyboard-
        // scan loop (software-timed, not something this phase pins to an
        // exact tick count) to see the key down and react to it.
        for (var i = 0; i < 3_000_000; i++)
        {
            system.Tick();
        }

        var withKeyHeld = WhiteSamples(television);

        system.OnKeyEvent(new KeyEvent { IsDown = false, Key = Key.A, Character = 'a' });

        await Assert.That(withKeyHeld).IsNotEqualTo(baseline);
    }
}
