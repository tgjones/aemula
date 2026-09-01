using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Aemula.Emulation.Output;

namespace Aemula.Tests.Emulation.Output;

// Like TelevisionTests, the bar here is "recognizably correct", not
// DSP-lab accuracy: tolerances are deliberately wide and the checks are
// about direction and rough magnitude (a 1 kHz tone stays 1 kHz and near
// its input amplitude; an ultrasonic tone comes out quiet; positive trim
// yields more samples) rather than exact filter responses.
public class AudioOutputTests
{
    private const double TiaLikeInputRate = 31_400.0;

    [Test]
    public async Task OneKilohertzToneSurvivesResamplingInFrequencyAndAmplitude()
    {
        var audio = new AudioOutput(TiaLikeInputRate);

        const double toneHz = 1_000.0;
        const float amplitude = 0.5f;
        var output = ProcessTone(audio, toneHz, amplitude, TiaLikeInputRate, seconds: 0.5);

        // Skip the filter fill / resampler priming transient.
        var steady = output.AsSpan(2_000);

        var atTone = GoertzelAmplitude(steady, toneHz, AudioOutput.OutputSampleRate);
        var wellBelow = GoertzelAmplitude(steady, 200.0, AudioOutput.OutputSampleRate);
        var wellAbove = GoertzelAmplitude(steady, 5_000.0, AudioOutput.OutputSampleRate);

        // The 1 kHz component dominates.
        await Assert.That(atTone).IsGreaterThan(wellBelow * 5.0);
        await Assert.That(atTone).IsGreaterThan(wellAbove * 5.0);

        // Amplitude preserved within +/-25%.
        await Assert.That(atTone).IsGreaterThan(amplitude * 0.75);
        await Assert.That(atTone).IsLessThan(amplitude * 1.25);
    }

    [Test]
    public async Task UltrasonicToneIsAttenuatedRatherThanAliased()
    {
        // Source running well above 48 kHz so a 30 kHz tone is a legal
        // input, but still above the output Nyquist - without the
        // anti-alias low-pass it would fold down to a loud ~18 kHz (and,
        // after further resampling error, lower) tone.
        const double inputRate = 96_000.0;
        var audio = new AudioOutput(inputRate);

        const float amplitude = 0.5f;
        var output = ProcessTone(audio, 30_000.0, amplitude, inputRate, seconds: 0.3);

        var steady = output.AsSpan(2_000);
        var rms = Rms(steady);

        // Input RMS was ~0.354; the filter should crush this to near
        // silence. Anything below a few percent means no strong alias tone
        // got through.
        await Assert.That(rms).IsLessThan(0.05);
    }

    [Test]
    public async Task ReadOnEmptyBufferReturnsZeroAndWritesSilence()
    {
        var audio = new AudioOutput(TiaLikeInputRate);

        var destination = new float[256];
        Array.Fill(destination, 1f);

        var produced = audio.Read(destination);

        await Assert.That(produced).IsEqualTo(0);
        await Assert.That(audio.AvailableOutputSamples).IsEqualTo(0);
        foreach (var sample in destination)
        {
            await Assert.That(sample).IsEqualTo(0f);
        }
    }

    [Test]
    public async Task PositiveResampleTrimRaisesOutputSampleCount()
    {
        // Same input fed to both; the only difference is the trim.
        const int inputSampleCount = 10_000;
        var tone = BuildTone(1_000.0, 0.5f, TiaLikeInputRate, inputSampleCount);

        var baseline = new AudioOutput(TiaLikeInputRate);
        var baselineCount = FeedAndDrain(baseline, tone);

        var trimmed = new AudioOutput(TiaLikeInputRate);
        trimmed.SetResampleTrim(0.02);
        var trimmedCount = FeedAndDrain(trimmed, tone);

        await Assert.That(trimmedCount).IsGreaterThan(baselineCount);
    }

    [Test]
    public async Task DcBlockerDecaysConstantInputToZero()
    {
        var audio = new AudioOutput(TiaLikeInputRate);

        // A couple of seconds of a constant nonzero level - far longer than
        // the DC blocker's few-millisecond time constant.
        var constant = new float[(int)(TiaLikeInputRate * 2)];
        Array.Fill(constant, 0.6f);

        var output = FeedAndDrainSamples(audio, constant);
        await Assert.That(output.Count).IsGreaterThan(1_000);

        // The steady-state output (the last chunk) has settled back to ~0.
        var tail = output.GetRange(output.Count - 500, 500).ToArray();
        await Assert.That(Rms(tail)).IsLessThan(0.02);
        await Assert.That(Math.Abs(Mean(tail))).IsLessThan(0.01);
    }

    [Test]
    public async Task NullAudioSourceReadIsAlwaysZeroAndSilent()
    {
        var destination = new float[128];
        Array.Fill(destination, 1f);

        var produced = NullAudioSource.Instance.Read(destination);

        await Assert.That(produced).IsEqualTo(0);
        await Assert.That(NullAudioSource.Instance.AvailableOutputSamples).IsEqualTo(0);
        foreach (var sample in destination)
        {
            await Assert.That(sample).IsEqualTo(0f);
        }

        // The remaining members are harmless no-ops.
        NullAudioSource.Instance.SetResampleTrim(0.01);
        NullAudioSource.Instance.Reset();
        NullAudioSource.Instance.MasterVolume = 0.5f;
    }

    // --- helpers ---

    private static float[] BuildTone(double toneHz, float amplitude, double sampleRate, int sampleCount)
    {
        var samples = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            samples[i] = (float)(amplitude * Math.Sin(2.0 * Math.PI * toneHz * i / sampleRate));
        }

        return samples;
    }

    // Writes a tone of the given duration, draining a frame's worth of
    // output every ~1 ms of input so the ring's backlog cap never trips -
    // mimics how a real consumer interleaves WriteSample and Read.
    private static float[] ProcessTone(
        AudioOutput audio, double toneHz, float amplitude, double inputRate, double seconds)
    {
        var totalInput = (int)(inputRate * seconds);
        var chunk = Math.Max(1, (int)(inputRate / 1_000.0));
        var scratch = new float[chunk * 4];
        var collected = new List<float>();

        var phase = 0.0;
        var increment = 2.0 * Math.PI * toneHz / inputRate;

        for (var written = 0; written < totalInput; written++)
        {
            audio.WriteSample((float)(amplitude * Math.Sin(phase)));
            phase += increment;

            if (written % chunk == chunk - 1)
            {
                int produced;
                while ((produced = audio.Read(scratch)) > 0)
                {
                    collected.AddRange(new ReadOnlySpan<float>(scratch, 0, produced).ToArray());
                    if (produced < scratch.Length)
                    {
                        break;
                    }
                }
            }
        }

        // Final drain.
        int last;
        while ((last = audio.Read(scratch)) > 0)
        {
            collected.AddRange(new ReadOnlySpan<float>(scratch, 0, last).ToArray());
            if (last < scratch.Length)
            {
                break;
            }
        }

        return collected.ToArray();
    }

    private static int FeedAndDrain(AudioOutput audio, float[] input)
    {
        return FeedAndDrainSamples(audio, input).Count;
    }

    private static List<float> FeedAndDrainSamples(AudioOutput audio, float[] input)
    {
        var scratch = new float[4_096];
        var collected = new List<float>();
        var chunk = 1_000;

        for (var i = 0; i < input.Length; i++)
        {
            audio.WriteSample(input[i]);

            if (i % chunk == chunk - 1)
            {
                Drain(audio, scratch, collected);
            }
        }

        Drain(audio, scratch, collected);
        return collected;
    }

    private static void Drain(AudioOutput audio, float[] scratch, List<float> into)
    {
        int produced;
        while ((produced = audio.Read(scratch)) > 0)
        {
            for (var j = 0; j < produced; j++)
            {
                into.Add(scratch[j]);
            }

            if (produced < scratch.Length)
            {
                break;
            }
        }
    }

    // Amplitude of the sinusoidal component at freq, via the Goertzel
    // algorithm (single-bin DFT).
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

    private static double Mean(ReadOnlySpan<float> samples)
    {
        double sum = 0.0;
        for (var i = 0; i < samples.Length; i++)
        {
            sum += samples[i];
        }

        return sum / Math.Max(samples.Length, 1);
    }
}
