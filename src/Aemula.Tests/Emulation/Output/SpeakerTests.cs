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
    public async Task SingleEdgeProducesABoundedClickThatDecaysBackToRest()
    {
        var speaker = new Speaker(AppleIiTickRate);

        // Advance to a known position, then a single rising edge, then run on
        // long enough that the DC blocker (~20 Hz, ~8 ms time constant) has
        // fully relaxed the held level back toward zero.
        for (var t = 0; t < 20_000; t++)
        {
            speaker.Tick();
        }

        speaker.Level = true;

        for (var t = 0; t < 1_500_000; t++)
        {
            speaker.Tick();
        }

        var samples = Drain(speaker);

        // The edge sits near output sample 71 (4 + 20000 * 48000/14318180);
        // its BLEP window is BlepHalfWidth (4) samples either side. Everything
        // comfortably before that window is untouched silence.
        var leadRms = Rms(new ReadOnlySpan<float>(samples, 0, 55));

        // The click itself: a bounded excursion toward +Amplitude right after
        // the edge, overshooting only a few percent on the short BLEP kernel.
        var clickPeak = 0.0;
        for (var i = 60; i < 400; i++)
        {
            clickPeak = Math.Max(clickPeak, samples[i]);
        }

        // ... which a directly-driven cone does not hold: far past the edge the
        // DC blocker has bled the level away, leaving no sound and no pedestal.
        var tail = samples[^500..];
        var tailRms = Rms(tail);
        var tailMean = Mean(tail);

        var peak = 0.0;
        foreach (var s in samples)
        {
            peak = Math.Max(peak, Math.Abs(s));
        }

        await Assert.That(samples.Length).IsGreaterThan(150);
        await Assert.That(leadRms).IsLessThan(0.02);
        await Assert.That(clickPeak).IsGreaterThan(0.5);
        await Assert.That(clickPeak).IsLessThan(SettledLevel * 1.08);
        // A short BLEP kernel rings a few percent past its target on the edge.
        await Assert.That(peak).IsLessThan(SettledLevel * 1.08);
        await Assert.That(tailRms).IsLessThan(0.01);
        await Assert.That(Math.Abs(tailMean)).IsLessThan(0.005);
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
        // Reference: one full -Amplitude -> +Amplitude transition (a 2*A
        // step). PrimeToLow leaves the cone settled at -Amplitude with the DC
        // blocker fully relaxed and all prior output already drained, so the
        // click captured below is this transition's alone.
        var single = PrimeToLow(new Speaker(AppleIiTickRate));
        single.Level = true;
        var singlePeak = PeakAbs(RunAndDrain(single));

        // Two opposite edges in one output sample from that same primed-low
        // state: +2A then -2A sum to nothing, so no band-limited step is
        // spliced at all - the output stays at rest, no click.
        var cancelling = PrimeToLow(new Speaker(AppleIiTickRate));
        cancelling.Level = true;
        cancelling.Level = false; // same tick count -> same output sample
        var cancellingPeak = PeakAbs(RunAndDrain(cancelling));

        // Three edges in one output sample from the primed-low state
        // (low->high->low->high): +2A, -2A, +2A sum to a single net +2A step,
        // so the click matches the single-transition reference - not doubled,
        // not cancelled to nothing.
        var tripled = PrimeToLow(new Speaker(AppleIiTickRate));
        tripled.Level = true;
        tripled.Level = false;
        tripled.Level = true;
        var tripledPeak = PeakAbs(RunAndDrain(tripled));

        await Assert.That(cancellingPeak).IsLessThan(singlePeak * 0.05);
        await Assert.That(Math.Abs(tripledPeak - singlePeak)).IsLessThan(singlePeak * 0.08);
    }

    // Long enough for the DC blocker (~381 output samples time constant) to
    // relax a held level essentially to zero.
    private const int DecayTicks = 400_000;

    // Toggle once to +Amplitude and back to -Amplitude, letting each step fully
    // decay, then drain everything produced so far. Leaves the cone's last
    // settled level at -Amplitude (so _currentLevel is -A, not the fresh-Speaker
    // 0, and a following transition is a full 2*A swing) with the output ring
    // flushed and the DC blocker at rest.
    private static Speaker PrimeToLow(Speaker speaker)
    {
        speaker.Level = true;
        Advance(speaker, DecayTicks);
        speaker.Level = false;
        Advance(speaker, DecayTicks);
        Drain(speaker);
        return speaker;
    }

    // Run DecayTicks ticks so the just-set transition's click plays out and
    // decays, then return everything drained.
    private static float[] RunAndDrain(Speaker speaker)
    {
        Advance(speaker, DecayTicks);
        return Drain(speaker);
    }

    private static void Advance(Speaker speaker, int ticks)
    {
        for (var t = 0; t < ticks; t++)
        {
            speaker.Tick();
        }
    }

    private static double PeakAbs(float[] samples)
    {
        var peak = 0.0;
        foreach (var s in samples)
        {
            peak = Math.Max(peak, Math.Abs(s));
        }

        return peak;
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
