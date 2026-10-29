using System;
using System.Threading.Tasks;
using Aemula.Emulation.Systems.BbcMicro;

namespace Aemula.Tests.Emulation.Systems.BbcMicro;

// BASIC's SOUND statement reaches the SN76489 through System VIA port A and
// the addressable latch's write-enable bit.
public class BbcMicroAudioTests
{
    private const uint ShortResetPulseTicks = 100;

    private static void Run(BbcMicroSystem system, int ticks)
    {
        for (var tick = 0; tick < ticks; tick++)
        {
            system.Tick();
        }
    }

    [Test]
    public async Task Sound_statement_plays_the_requested_pitch()
    {
        var system = new BbcMicroSystem(null, ShortResetPulseTicks);
        Run(system, 12_000_000);

        // Pitch 53 is middle C (261.6Hz) and 48 is an octave, so 101 is 523.3Hz; the note lasts
        // 100/20 = 5 seconds.
        foreach (var character in "SOUND 1,-15,101,100\r")
        {
            var key = new KeyEvent { IsDown = true, Key = (Key)character, Character = character };
            system.OnKeyEvent(key);
            Run(system, 1_600_000);
            system.OnKeyEvent(new KeyEvent { IsDown = false, Key = (Key)character, Character = character });
            Run(system, 1_600_000);
        }

        // Discard what played while typing, then listen to half a second.
        var drain = new float[48_000];
        system.Audio!.Read(drain);
        Run(system, 8_000_000);

        var samples = new float[system.Audio.AvailableOutputSamples];
        var count = system.Audio.Read(samples);

        var crossings = 0;
        var peak = 0f;
        for (var i = 1; i < count; i++)
        {
            peak = Math.Max(peak, Math.Abs(samples[i]));
            if ((samples[i - 1] < 0) != (samples[i] < 0))
            {
                crossings++;
            }
        }

        var seconds = count / 48_000.0;
        var frequency = crossings / 2.0 / seconds;

        await Assert.That(peak).IsGreaterThan(0.1f);
        await Assert.That(frequency).IsEqualTo(523.3).Within(25.0);
    }
}
