using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Aemula.Emulation.Output;
using Aemula.Emulation.Systems.Atari2600;

namespace Aemula.Tests.Emulation.Systems.Atari2600;

// The code-verified "does the 2600 actually make the right sound" check, the
// audio counterpart of Atari2600SystemTelevisionTests: hand-assemble a 4K
// cartridge that programs TIA's audio registers and then loops forever, run
// it for a few seconds of emulated time, pull the produced audio back out
// through the same IAudioSource the UI uses, and analyse it with a Goertzel
// single-bin DFT.
//
// Like TelevisionTests / AudioOutputTests, the bar is "recognizably
// correct", not DSP-lab accuracy: a divide-by-two tone lands near its
// predicted frequency and dominates the spectrum, a zero-volume channel is
// silent, and a constant-output waveform decodes to silence once the DC
// blocker has settled.
public class Atari2600AudioTests
{
    // TIA audio write-register addresses (see Atari2600Debugger's Equates).
    private const byte Audc0 = 0x15;
    private const byte Audf0 = 0x17;
    private const byte Audv0 = 0x19;

    private const ushort CodeStart = 0x1000;

    // 3.58 MHz OSC / 114 (two audio clocks per 228-OSC scanline) - the rate
    // TIA feeds AudioOutput at, and the reference the expected tone
    // frequency below is derived from. Kept in sync with
    // Atari2600System.CyclesPerSecond by construction.
    private const double AudioInputRate = 3_580_000.0 / 114.0;

    // A cartridge that writes AUDC0/AUDF0/AUDV0 once and then spins on a
    // JMP-to-self, so the audio registers stay put for as long as the test
    // runs. A free-running open-bus CPU would eventually stomp them, hence a
    // real program rather than poked memory.
    private static byte[] BuildAudioCartridge(byte audc, byte audf, byte audv)
    {
        var rom = new byte[4096];

        // LDA #audc / STA AUDC0 / LDA #audf / STA AUDF0 / LDA #audv /
        // STA AUDV0 / JMP self. The JMP instruction sits at CodeStart + 12,
        // so it targets its own address ($100C) and the CPU spins there.
        var code = new byte[]
        {
            0xA9, audc, 0x85, Audc0,
            0xA9, audf, 0x85, Audf0,
            0xA9, audv, 0x85, Audv0,
            0x4C, (CodeStart + 12) & 0xFF, (CodeStart + 12) >> 8,
        };
        code.CopyTo(rom, 0);

        // Reset vector ($FFFC/$FFFD, masked to the 4K image's $FFC/$FFD) ->
        // CodeStart.
        rom[0xFFC] = CodeStart & 0xFF;
        rom[0xFFD] = CodeStart >> 8;

        return rom;
    }

    private static string WriteCartridgeToTempFile(byte[] rom)
    {
        var path = Path.Combine(Path.GetTempPath(), $"aemula-atari2600-audio-test-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, rom);
        return path;
    }

    // Run frameCount frames, draining the resampled 48 kHz output through the
    // IAudioSource once per frame (mirrors how the emulation window pumps it)
    // and returning everything collected.
    private static float[] RunAndCollectAudio(Atari2600System system, int frameCount)
    {
        // 262 lines * 228 OSC ticks/line, same as the Television tests.
        const int ticksPerFrame = 262 * 228;

        var collected = new List<float>();
        var scratch = new float[8192];

        for (var frame = 0; frame < frameCount; frame++)
        {
            for (var i = 0; i < ticksPerFrame; i++)
            {
                system.Tick();
            }

            int produced;
            while ((produced = system.Audio.Read(scratch)) > 0)
            {
                for (var j = 0; j < produced; j++)
                {
                    collected.Add(scratch[j]);
                }

                if (produced < scratch.Length)
                {
                    break;
                }
            }
        }

        return collected.ToArray();
    }

    // Enough frames to leave well over 20,000 output samples for analysis
    // after the startup skip: ~524 audio ticks/frame * 160 frames * 48000 /
    // AudioInputRate ~= 128,000 output samples.
    private const int Frames = 160;

    // Output samples discarded before every measurement, so Television/Audio
    // self-calibration and AudioOutput's DC-blocker settling transient are
    // well past (~5 frames of audio).
    private const int SkipSamples = 8_000;

    [Test]
    public async Task Audc4ProducesADivideByTwoToneNearItsPredictedFrequency()
    {
        // AUDC 4 is a pure divide-by-two: the channel output flips every
        // (AUDF + 1) audio ticks, so the fundamental is
        // AudioInputRate / (2 * (AUDF + 1)) = ~981 Hz at AUDF 15.
        const double expectedHz = AudioInputRate / (2.0 * (15 + 1));

        var system = new Atari2600System();
        var path = WriteCartridgeToTempFile(BuildAudioCartridge(audc: 4, audf: 15, audv: 15));

        try
        {
            system.LoadProgram(path);
            system.Reset();

            var output = RunAndCollectAudio(system, Frames);
            await Assert.That(output.Length).IsGreaterThan(SkipSamples + 20_000);

            var steady = output[SkipSamples..];

            // Coarse sweep to report where the fundamental actually landed.
            var measuredHz = 0.0;
            var measuredAmp = 0.0;
            for (var hz = 850.0; hz <= 1120.0; hz += 2.0)
            {
                var amp = GoertzelAmplitude(steady, hz, AudioOutput.OutputSampleRate);
                if (amp > measuredAmp)
                {
                    measuredAmp = amp;
                    measuredHz = hz;
                }
            }

            Console.WriteLine(
                $"AUDC4 fundamental: measured ~{measuredHz:F1} Hz, expected ~{expectedHz:F1} Hz");

            // Recognizably the right note (wide tolerance - the sweep step
            // itself is 2 Hz, and the bar is "the tone is there", not a
            // frequency-counter reading).
            await Assert.That(Math.Abs(measuredHz - expectedHz)).IsLessThan(25.0);

            // ... and it dominates the spectrum well above and below it. 500
            // and 1500 Hz are neither the fundamental nor one of the square
            // wave's odd harmonics (~2944 Hz and up).
            var atTone = GoertzelAmplitude(steady, expectedHz, AudioOutput.OutputSampleRate);
            var below = GoertzelAmplitude(steady, 500.0, AudioOutput.OutputSampleRate);
            var above = GoertzelAmplitude(steady, 1_500.0, AudioOutput.OutputSampleRate);

            await Assert.That(atTone).IsGreaterThan(below * 6.0);
            await Assert.That(atTone).IsGreaterThan(above * 6.0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Audv0IsSilent()
    {
        // AUDC 4 would be a loud tone, but AUDV 0 forces every channel
        // sample to 0, so the summing stage feeds AudioOutput a flat zero.
        var system = new Atari2600System();
        var path = WriteCartridgeToTempFile(BuildAudioCartridge(audc: 4, audf: 15, audv: 0));

        try
        {
            system.LoadProgram(path);
            system.Reset();

            var output = RunAndCollectAudio(system, Frames);
            await Assert.That(output.Length).IsGreaterThan(SkipSamples + 20_000);

            var steady = output[SkipSamples..];
            await Assert.That(Rms(steady)).IsLessThan(0.005);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Audc0IsSilent()
    {
        // AUDC 0 is a "set to 1" hold mode: after the power-on transient the
        // channel output never moves again, so the signal into AudioOutput
        // is pure DC - which its DC blocker removes, leaving near silence.
        var system = new Atari2600System();
        var path = WriteCartridgeToTempFile(BuildAudioCartridge(audc: 0, audf: 15, audv: 15));

        try
        {
            system.LoadProgram(path);
            system.Reset();

            var output = RunAndCollectAudio(system, Frames);
            await Assert.That(output.Length).IsGreaterThan(SkipSamples + 20_000);

            var steady = output[SkipSamples..];
            await Assert.That(Rms(steady)).IsLessThan(0.01);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // --- helpers (Goertzel / RMS copied from AudioOutputTests) ---

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
}
