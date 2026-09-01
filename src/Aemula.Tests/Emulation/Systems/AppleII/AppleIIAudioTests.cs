using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Aemula.Emulation.Output;
using Aemula.Emulation.Systems.AppleII;

namespace Aemula.Tests.Emulation.Systems.AppleII;

// The code-verified "does the Apple II speaker actually make the right sound"
// check, the audio counterpart of AppleIISystemTelevisionTests. Unlike the
// 2600, the Apple II has no audio-out signal: $C030 clocks a flip-flop whose Q
// drives a transistor wired straight to the speaker cone, so a program makes a
// tone purely by strobing $C030 at the right rate. This drives that strobe
// directly (ReadByteDebug(0xC030) routes through the real decode and calls
// ToggleSpeaker, exactly as AppleIISystemGameIoTests exercises it), builds a
// square wave by interleaving tick batches with toggles, pulls the produced
// audio back through the same IAudioSource the UI uses, and analyses it with a
// Goertzel single-bin DFT.
//
// Same bar as SpeakerTests / TelevisionTests / AudioOutputTests: "recognizably
// correct", not a frequency counter. A periodic toggle lands near its predicted
// pitch and dominates the spectrum; a machine left alone is silent.
public class AppleIIAudioTests
{
    // The Apple II master clock (AppleIISystem.CyclesPerSecond), the rate
    // Tick() - and therefore the speaker's free-running position - advances at.
    private const double MasterClock = 14_318_180.0;

    // $C030 speaker toggle.
    private const ushort SpeakerToggle = 0xC030;

    // Master ticks between successive $C030 strobes. A full square-wave period
    // is 2 * this, so the fundamental is MasterClock / (2 * 7159) ~= 1000 Hz -
    // a clean, comfortably in-band value (matches SpeakerTests' own choice).
    private const int TicksPerHalfPeriod = 7_159;
    private const double ExpectedHz = MasterClock / (2.0 * TicksPerHalfPeriod);

    // Half-periods to run. ~24 output samples come out per half-period
    // (7159 * 48000 / 14_318_180), so 3000 of them leave ~72,000 output
    // samples - well over 20,000 for analysis after the startup skip.
    private const int HalfPeriods = 3_000;

    // Output samples discarded before every measurement: the first strobe
    // swings the cone from rest rather than symmetrically, and a cold boot's
    // own activity (if any) needs to be well past. ~1/6 s of audio.
    private const int SkipSamples = 8_000;

    private static AppleIISystem BootToIdle()
    {
        var system = new AppleIISystem();
        system.LoadProgram("");

        // Enough to get the Autostart ROM into its keyboard-wait loop, which
        // touches none of the game-I/O soft switches (so it won't move the
        // speaker on its own) - matching AppleIISystemGameIoTests' budget.
        for (var i = 0; i < 500_000; i++)
        {
            system.Tick();
        }

        return system;
    }

    [Test]
    public async Task StrobingC030PeriodicallyProducesAToneNearItsPredictedFrequency()
    {
        var system = BootToIdle();

        var collected = new List<float>();
        var scratch = new float[8_192];

        for (var half = 0; half < HalfPeriods; half++)
        {
            for (var t = 0; t < TicksPerHalfPeriod; t++)
            {
                system.Tick();
            }

            // One edge on the speaker pin.
            system.ReadByteDebug(SpeakerToggle);

            Drain(system.Audio, scratch, collected);
        }

        // Trailing ticks so the last edges' band-limited steps finalise.
        for (var t = 0; t < 50_000; t++)
        {
            system.Tick();
        }

        Drain(system.Audio, scratch, collected);

        var output = collected.ToArray();
        await Assert.That(output.Length).IsGreaterThan(SkipSamples + 20_000);

        var steady = output[SkipSamples..];

        // Coarse sweep to report where the fundamental actually landed.
        var measuredHz = 0.0;
        var measuredAmp = 0.0;
        for (var hz = 900.0; hz <= 1_100.0; hz += 2.0)
        {
            var amp = GoertzelAmplitude(steady, hz, Speaker.OutputSampleRate);
            if (amp > measuredAmp)
            {
                measuredAmp = amp;
                measuredHz = hz;
            }
        }

        Console.WriteLine(
            $"$C030 tone: measured ~{measuredHz:F1} Hz, expected ~{ExpectedHz:F1} Hz");

        // Recognizably the right note (wide tolerance - the sweep step is 2 Hz
        // and the bar is "the tone is there").
        await Assert.That(Math.Abs(measuredHz - ExpectedHz)).IsLessThan(25.0);

        // ... and it dominates the spectrum well above and below it. 300 Hz is
        // well below the fundamental; 1700 Hz sits in the null between it and
        // the square wave's 3rd harmonic (~3000 Hz).
        var atTone = GoertzelAmplitude(steady, ExpectedHz, Speaker.OutputSampleRate);
        var below = GoertzelAmplitude(steady, 300.0, Speaker.OutputSampleRate);
        var above = GoertzelAmplitude(steady, 1_700.0, Speaker.OutputSampleRate);

        await Assert.That(atTone).IsGreaterThan(below * 5.0);
        await Assert.That(atTone).IsGreaterThan(above * 4.0);

        // A +/-0.6 square's fundamental is 4 * 0.6 / pi ~= 0.76 - loosely
        // bounded, but far enough above zero to tell it apart from silence.
        await Assert.That(atTone).IsGreaterThan(0.3);
        await Assert.That(atTone).IsLessThan(1.1);
    }

    [Test]
    public async Task NoStrobesReadsAsSilence()
    {
        var system = BootToIdle();

        var collected = new List<float>();
        var scratch = new float[8_192];

        // The same run length as the tone test, but the speaker pin is never
        // touched after boot - so the output must not move: no tone, no
        // fluctuation, just a flat, unchanging level.
        for (var half = 0; half < HalfPeriods; half++)
        {
            for (var t = 0; t < TicksPerHalfPeriod; t++)
            {
                system.Tick();
            }

            Drain(system.Audio, scratch, collected);
        }

        Drain(system.Audio, scratch, collected);

        var output = collected.ToArray();
        await Assert.That(output.Length).IsGreaterThan(SkipSamples + 20_000);

        var steady = output[SkipSamples..];

        // The Apple II+ Autostart ROM rings BELL once during cold boot, which
        // can leave the flip-flop (and so the cone) parked at one polarity's
        // full excursion. Speaker's DC blocker relaxes any held level back to
        // zero within a few milliseconds - a real AC-coupled cone does the same
        // - so after the startup skip the output is flat silence, no residual
        // pedestal and no fluctuation.
        await Assert.That(Rms(steady)).IsLessThan(0.005);
        await Assert.That(AcRms(steady)).IsLessThan(0.005);
    }

    // --- helpers ---

    private static void Drain(IAudioSource source, float[] scratch, List<float> into)
    {
        int produced;
        while ((produced = source.Read(scratch)) > 0)
        {
            for (var i = 0; i < produced; i++)
            {
                into.Add(scratch[i]);
            }

            if (produced < scratch.Length)
            {
                break;
            }
        }
    }

    // Amplitude of the sinusoidal component at freq, via the Goertzel
    // algorithm (single-bin DFT). Same helper as AudioOutputTests / SpeakerTests.
    private static double GoertzelAmplitude(ReadOnlySpan<float> samples, double freq, double sampleRate)
    {
        var n = samples.Length;
        var omega = 2.0 * Math.PI * freq / sampleRate;
        var coeff = 2.0 * Math.Cos(omega);

        double sPrev = 0.0;
        double sPrev2 = 0.0;
        for (var i = 0; i < n; i++)
        {
            var s = samples[i] + coeff * sPrev - sPrev2;
            sPrev2 = sPrev;
            sPrev = s;
        }

        var power = sPrev * sPrev + sPrev2 * sPrev2 - coeff * sPrev * sPrev2;
        return 2.0 * Math.Sqrt(Math.Max(power, 0.0)) / n;
    }

    private static double Rms(ReadOnlySpan<float> samples)
    {
        double sum = 0.0;
        for (var i = 0; i < samples.Length; i++)
        {
            sum += (double)samples[i] * samples[i];
        }

        return Math.Sqrt(sum / Math.Max(samples.Length, 1));
    }

    // RMS of the signal with its DC component removed - "how much does it move",
    // independent of what fixed level it is sitting at.
    private static double AcRms(ReadOnlySpan<float> samples)
    {
        double mean = 0.0;
        for (var i = 0; i < samples.Length; i++)
        {
            mean += samples[i];
        }

        mean /= Math.Max(samples.Length, 1);

        double sum = 0.0;
        for (var i = 0; i < samples.Length; i++)
        {
            var d = samples[i] - mean;
            sum += d * d;
        }

        return Math.Sqrt(sum / Math.Max(samples.Length, 1));
    }
}
