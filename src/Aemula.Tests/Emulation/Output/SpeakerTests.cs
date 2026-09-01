using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Aemula.Emulation.Output;

namespace Aemula.Tests.Emulation.Output;

// Same bar as AudioOutputTests and TelevisionTests: "recognizably correct",
// not DSP-lab accuracy. The checks are about direction and rough magnitude -
// a single edge produces a bounded click that settles to the new level, a
// periodic toggle comes back at the right pitch, positive trim yields more
// samples - with deliberately wide tolerances.
public class SpeakerTests
{
    // The Apple II master clock, the rate Tick() is driven at there.
    private const double AppleIiTickRate = 14_318_180.0;

    private const float SettledLevel = (float)Speaker.Amplitude;

    [Test]
    public async Task SingleEdgeProducesBoundedClickThatSettlesToNewLevel()
    {
        var speaker = new Speaker(AppleIiTickRate);

        // Advance to a known position, then a single rising edge, then run on
        // well past the kernel so everything the edge touched is finalised.
        for (var t = 0; t < 20_000; t++)
        {
            speaker.Tick();
        }

        speaker.Level = true;

        // Long enough afterwards that the settled tail sampled below is many
        // hundreds of samples clear of the edge.
        for (var t = 0; t < 400_000; t++)
        {
            speaker.Tick();
        }

        var samples = Drain(speaker);

        // The edge sits near output sample 71 (4 + 20000 * 48000/14318180);
        // its BLEP window is BlepHalfWidth (4) samples either side. Everything
        // comfortably before that window is untouched silence.
        var leadRms = Rms(new ReadOnlySpan<float>(samples, 0, 55));

        // Everything comfortably after it has settled to the new DC level and
        // stays flat there (no slow drift, no ringing tail).
        var tailMean = Mean(new ReadOnlySpan<float>(samples, samples.Length - 200, 200));
        var tailDeviation = 0.0;
        for (var i = samples.Length - 200; i < samples.Length; i++)
        {
            tailDeviation = Math.Max(tailDeviation, Math.Abs(samples[i] - tailMean));
        }

        // Total excursion is bounded: the windowed step overshoots its target
        // by well under a percent, nothing runs away.
        var peak = 0.0;
        foreach (var s in samples)
        {
            peak = Math.Max(peak, Math.Abs(s));
        }

        await Assert.That(samples.Length).IsGreaterThan(150);
        await Assert.That(leadRms).IsLessThan(0.02);
        await Assert.That(tailMean).IsGreaterThan(0.5);
        await Assert.That(tailMean).IsLessThan(0.8);
        await Assert.That(tailDeviation).IsLessThan(0.01);
        // A short BLEP kernel rings a few percent past its target on the edge.
        await Assert.That(peak).IsLessThan(Math.Abs(tailMean) * 1.08);

        // Settled within the kernel width: sample 90 is well past the last tap
        // (which lands near sample 75).
        await Assert.That(Math.Abs(samples[90] - tailMean)).IsLessThan(0.02);
    }

    [Test]
    public async Task PeriodicToggleReproducesFundamentalAt48k()
    {
        var speaker = new Speaker(AppleIiTickRate);

        // Toggle every 7159 ticks -> full square-wave period 14318 ticks ->
        // fundamental AppleIiTickRate / 14318 ~= 1000 Hz.
        const int toggleInterval = 7_159;
        const double expectedHz = AppleIiTickRate / (2 * toggleInterval);

        var samples = RunTicks(speaker, totalTicks: 7_159_090, toggleInterval);

        // Skip the start-up transient (the first half-cycle rises from rest,
        // not from -level).
        var atTone = GoertzelAmplitude(new ReadOnlySpan<float>(samples, 2_000, samples.Length - 2_000), expectedHz, Speaker.OutputSampleRate);
        var wellBelow = GoertzelAmplitude(new ReadOnlySpan<float>(samples, 2_000, samples.Length - 2_000), 200.0, Speaker.OutputSampleRate);
        var wellAbove = GoertzelAmplitude(new ReadOnlySpan<float>(samples, 2_000, samples.Length - 2_000), 5_000.0, Speaker.OutputSampleRate);

        await Assert.That(samples.Length).IsGreaterThan(20_000);
        await Assert.That(atTone).IsGreaterThan(wellBelow * 5.0);
        await Assert.That(atTone).IsGreaterThan(wellAbove * 3.0);

        // A +/-0.6 square's fundamental is 4 * 0.6 / pi ~= 0.76.
        await Assert.That(atTone).IsGreaterThan(0.5);
        await Assert.That(atTone).IsLessThan(1.05);
    }

    [Test]
    public async Task NoEdgesReadsAsSilence()
    {
        var speaker = new Speaker(AppleIiTickRate);

        // Nothing ticked yet: behaves exactly like an empty AudioOutput.
        var destination = new float[256];
        Array.Fill(destination, 1f);
        var produced = speaker.Read(destination);
        var availableBeforeTicks = speaker.AvailableOutputSamples;
        var allSilentBeforeTicks = AllZero(destination);

        // Time passes but the pin is never touched: still pure silence, just
        // now actually produced rather than an underrun.
        for (var t = 0; t < 200_000; t++)
        {
            speaker.Tick();
        }

        Array.Fill(destination, 1f);
        speaker.Read(destination);
        var allSilentAfterTicks = AllZero(destination);

        await Assert.That(produced).IsEqualTo(0);
        await Assert.That(availableBeforeTicks).IsEqualTo(0);
        await Assert.That(allSilentBeforeTicks).IsTrue();
        await Assert.That(allSilentAfterTicks).IsTrue();
    }

    [Test]
    public async Task EdgesInTheSameOutputSampleComposeCorrectly()
    {
        // Two opposite edges at the same position cancel to almost nothing.
        var cancelling = new Speaker(AppleIiTickRate);
        Settle(cancelling, edgeToTrue: true, ticksBefore: 30_000, ticksAfter: 400_000);
        cancelling.Level = false;
        cancelling.Level = true; // same tick count -> same output sample
        for (var t = 0; t < 400_000; t++)
        {
            cancelling.Tick();
        }

        var cancelled = Drain(cancelling);
        var cancelledPeakDeviation = 0.0;
        for (var i = 120; i < cancelled.Length; i++)
        {
            cancelledPeakDeviation = Math.Max(cancelledPeakDeviation, Math.Abs(cancelled[i] - SettledLevel));
        }
        var cancelledTailMean = Mean(new ReadOnlySpan<float>(cancelled, cancelled.Length - 200, 200));

        // Two same-direction steps at the same position (with the opposite one
        // between them) add: -level to +level is a full 2*level swing,
        // delivered as +2A, -2A, +2A into identical slots, and it lands on
        // +level exactly once - not clipped short, not doubled past it.
        var adding = new Speaker(AppleIiTickRate);
        Settle(adding, edgeToTrue: true, ticksBefore: 30_000, ticksAfter: 400_000);
        adding.Level = false;
        for (var t = 0; t < 400_000; t++)
        {
            adding.Tick();
        }

        var beforeTriple = Drain(adding);
        var beforeTripleMean = Mean(new ReadOnlySpan<float>(beforeTriple, beforeTriple.Length - 100, 100));

        adding.Level = true;
        adding.Level = false;
        adding.Level = true;
        for (var t = 0; t < 400_000; t++)
        {
            adding.Tick();
        }

        var afterTriple = Drain(adding);
        var afterTripleMean = Mean(new ReadOnlySpan<float>(afterTriple, afterTriple.Length - 100, 100));
        var afterPeak = 0.0;
        foreach (var s in afterTriple)
        {
            afterPeak = Math.Max(afterPeak, Math.Abs(s));
        }

        await Assert.That(cancelledPeakDeviation).IsLessThan(0.1);
        await Assert.That(Math.Abs(cancelledTailMean - SettledLevel)).IsLessThan(0.02);
        await Assert.That(beforeTripleMean).IsLessThan(-0.5);
        await Assert.That(Math.Abs(afterTripleMean - SettledLevel)).IsLessThan(0.02);
        // Lands on +level once, bar the short kernel's few-percent edge ring -
        // not doubled past it.
        await Assert.That(afterPeak).IsLessThan(SettledLevel * 1.08);
    }

    [Test]
    public async Task PositiveResampleTrimRaisesOutputSampleCount()
    {
        const long totalTicks = 2_000_000;
        const int toggleInterval = 7_159;

        var baseline = new Speaker(AppleIiTickRate);
        var baselineCount = RunTicks(baseline, totalTicks, toggleInterval).Length;

        var trimmed = new Speaker(AppleIiTickRate);
        trimmed.SetResampleTrim(0.02);
        var trimmedCount = RunTicks(trimmed, totalTicks, toggleInterval).Length;

        await Assert.That(trimmedCount).IsGreaterThan(baselineCount);
    }

    // --- helpers ---

    private static bool AllZero(ReadOnlySpan<float> samples)
    {
        foreach (var s in samples)
        {
            if (s != 0f)
            {
                return false;
            }
        }

        return true;
    }

    private static void Settle(Speaker speaker, bool edgeToTrue, int ticksBefore, int ticksAfter)
    {
        for (var t = 0; t < ticksBefore; t++)
        {
            speaker.Tick();
        }

        speaker.Level = edgeToTrue;

        for (var t = 0; t < ticksAfter; t++)
        {
            speaker.Tick();
        }
    }

    private static float[] Drain(Speaker speaker)
    {
        var scratch = new float[4_096];
        var collected = new List<float>();
        DrainInto(speaker, scratch, collected);
        return collected.ToArray();
    }

    // Ticks the speaker totalTicks times, flipping Level every toggleInterval
    // ticks, draining output periodically so the backlog cap never trips -
    // mimics how a real consumer interleaves Tick and Read.
    private static float[] RunTicks(Speaker speaker, long totalTicks, int toggleInterval)
    {
        var scratch = new float[4_096];
        var collected = new List<float>();

        for (long t = 0; t < totalTicks; t++)
        {
            if (toggleInterval > 0 && t > 0 && t % toggleInterval == 0)
            {
                speaker.Level = !speaker.Level;
            }

            speaker.Tick();

            if (t % 1_000 == 999)
            {
                DrainInto(speaker, scratch, collected);
            }
        }

        DrainInto(speaker, scratch, collected);
        return collected.ToArray();
    }

    private static void DrainInto(Speaker speaker, float[] scratch, List<float> into)
    {
        int produced;
        while ((produced = speaker.Read(scratch)) > 0)
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
    // algorithm (single-bin DFT). Same helper as AudioOutputTests.
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
