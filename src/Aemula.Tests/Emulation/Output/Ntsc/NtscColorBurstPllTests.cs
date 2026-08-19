using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Aemula.Emulation.Output.Ntsc;

namespace Aemula.Tests.Emulation.Output.Ntsc;

public class NtscColorBurstPllTests
{
    private sealed record Pipeline(NtscSyncSeparator Separator, NtscRasterOscillators Oscillators, NtscColorBurstPll Pll)
    {
        public static Pipeline Create() => new(new NtscSyncSeparator(), new NtscRasterOscillators(), new NtscColorBurstPll());

        // Returns the PLL's PhaseOffsetRadians recorded at the end of each
        // line (i.e. once per completed line, in line order).
        public List<double> RunAndTracePhasePerLine(IReadOnlyList<byte> samples)
        {
            var phasePerLine = new List<double>();
            var lastColumn = 0;

            for (var i = 0; i < samples.Count; i++)
            {
                Separator.Process(samples[i]);
                Oscillators.Process(Separator.HSyncDetected, Separator.VSyncDetected);
                Pll.Process(samples[i], Oscillators.CurrentColumn, Separator.BlackLevel, Separator.WhiteLevel);

                if (Oscillators.CurrentColumn < lastColumn)
                {
                    phasePerLine.Add(Pll.PhaseOffsetRadians);
                }

                lastColumn = Oscillators.CurrentColumn;
            }

            return phasePerLine;
        }
    }

    [Test]
    public async Task DetectsBurstOnNearlyEveryLineOfSmpteAsset()
    {
        var samples = SmpteAsset.LoadNormalized();
        var pipeline = Pipeline.Create();

        var detectedCount = 0;
        var lineCount = 0;
        var lastColumn = 0;

        for (var i = 0; i < samples.Length; i++)
        {
            pipeline.Separator.Process(samples[i]);
            pipeline.Oscillators.Process(pipeline.Separator.HSyncDetected, pipeline.Separator.VSyncDetected);
            pipeline.Pll.Process(samples[i], pipeline.Oscillators.CurrentColumn, pipeline.Separator.BlackLevel, pipeline.Separator.WhiteLevel);

            if (pipeline.Oscillators.CurrentColumn < lastColumn)
            {
                lineCount++;
                if (pipeline.Pll.BurstDetected)
                {
                    detectedCount++;
                }
            }

            lastColumn = pipeline.Oscillators.CurrentColumn;
        }

        // 1050 lines total (see docs/television-plan.md's "Existing state");
        // ~37 of those are vertical-blanking lines with no real picture or
        // burst, matching real NTSC's vertical blanking interval - so
        // "nearly every" active line is checked with a generous, not exact,
        // threshold.
        await Assert.That(lineCount).IsEqualTo(1050);
        await Assert.That(detectedCount).IsGreaterThanOrEqualTo(950);
    }

    [Test]
    public async Task PhaseConvergesAndStabilizesOnConsistentSyntheticBurst()
    {
        const int lineLength = 910;
        const int hsyncWidth = 67;
        const byte syncSample = 0;
        const byte activeSample = 100;
        const double blackLevel = 64;
        const double burstAmplitude = 40;
        const double testPhaseRadians = 0.7; // arbitrary fixed "ground truth" phase, deliberately not 0
        const int lineCount = 40;

        var samples = new List<byte>();

        for (var line = 0; line < lineCount; line++)
        {
            for (var i = 0; i < hsyncWidth; i++)
            {
                samples.Add(syncSample);
            }

            for (var column = 0; column < lineLength - hsyncWidth; column++)
            {
                byte value;

                if (column >= NtscTiming.BurstWindowStartSamples
                    && column < NtscTiming.BurstWindowStartSamples + NtscTiming.BurstWindowLengthSamples)
                {
                    // Generated against the exact same "sample index mod 4"
                    // reference NtscColorBurstPll itself uses internally
                    // (samples.Count here is this sample's absolute index,
                    // matching the PLL's own free-running _sampleCounter at
                    // the point it processes this same sample) - so
                    // testPhaseRadians is, by construction, the true phase
                    // offset the PLL needs to converge to.
                    var absoluteIndex = samples.Count;
                    var truePhase = Math.PI / 2.0 * (absoluteIndex % 4) + testPhaseRadians;
                    value = (byte)Math.Clamp(blackLevel + burstAmplitude * Math.Sin(truePhase), 0, 255);
                }
                else
                {
                    value = activeSample;
                }

                samples.Add(value);
            }
        }

        var pipeline = Pipeline.Create();
        var phasePerLine = pipeline.RunAndTracePhasePerLine(samples);

        await Assert.That(pipeline.Pll.BurstDetected).IsTrue();
        await Assert.That(phasePerLine.Count).IsEqualTo(lineCount);

        // A consistent, unchanging burst phase every line should make the
        // loop's per-line correction shrink over time as it settles near
        // the true offset, rather than continuing to move by a similar
        // amount every line indefinitely.
        var earlyChange = Math.Abs(phasePerLine[2] - phasePerLine[1]);
        var lateChange = Math.Abs(phasePerLine[^1] - phasePerLine[^2]);

        await Assert.That(lateChange).IsLessThan(earlyChange * 0.1);
    }
}
