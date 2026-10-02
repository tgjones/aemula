using System;
using System.Threading.Tasks;
using Aemula.Emulation.Output;
using Aemula.Emulation.Output.Composite;

namespace Aemula.Tests.Emulation.Output;

public class TelevisionStandardTests
{
    private static Television Decode(TelevisionStandard standard, double samplesPerSecond, int fields)
    {
        var television = new Television(standard, (float)samplesPerSecond) { CaptureSampleDiagnostics = true };

        foreach (var sample in CompositeTestSignal.Generate(standard, samplesPerSecond, fields))
        {
            television.Decode(sample);
        }

        return television;
    }

    [Test]
    public async Task NtscTimingReproducesTheOriginalConstants()
    {
        var timing = TelevisionTiming.Ntsc;

        await Assert.That(timing.SamplesPerSecond).IsEqualTo(14_318_180f);
        await Assert.That(timing.HSyncWidthSamples).IsEqualTo(4.7e-6f * 14_318_180f);
        await Assert.That(timing.SamplesPerLine).IsEqualTo(63.5e-6f * 14_318_180f);
        await Assert.That(timing.LinesPerField).IsEqualTo(262.5f);
        await Assert.That(timing.BurstWindowStartSamples).IsEqualTo(0.6e-6f * 14_318_180f);
        await Assert.That(timing.BurstWindowLengthSamples).IsEqualTo(36f);
        await Assert.That(timing.FrontPorchFraction).IsEqualTo(1.5e-6f * 14_318_180f / (63.5e-6f * 14_318_180f));
    }

    [Test]
    public async Task PalTimingResolvesAgainstFourTimesItsSubcarrier()
    {
        var timing = new TelevisionTiming(TelevisionStandard.Pal);

        await Assert.That(Math.Abs(timing.SamplesPerSecond - 17_734_475f) < 2f).IsTrue();
        await Assert.That(Math.Abs(timing.SamplesPerLine - 1135f) < 0.1f).IsTrue();
        await Assert.That(timing.LinesPerField).IsEqualTo(312.5f);
        await Assert.That(Math.Abs(timing.BurstWindowLengthSamples - 40f) < 0.001f).IsTrue();
    }

    [Test]
    public async Task TimingScalesWithTheSampleRate()
    {
        var timing = new TelevisionTiming(TelevisionStandard.Pal, 6_500_000);

        await Assert.That(Math.Abs(timing.SamplesPerLine - 416f) < 0.01f).IsTrue();
        await Assert.That(Math.Abs(timing.HSyncWidthSamples - 30.55f) < 0.01f).IsTrue();
    }

    [Test]
    public async Task WhiteReferenceSitsAboveBlankingByTheStandardsPictureToSyncRatio()
    {
        // Sync 0, blanking at the byte scale's 224 / (1 + K): reference white
        // then lands back on 224 for both standards.
        foreach (var standard in new[] { TelevisionStandard.Ntsc, TelevisionStandard.Pal })
        {
            var blanking = 224f / (1 + standard.ReferenceWhiteGainFromSyncSwing);
            await Assert.That(Math.Abs(standard.WhiteReference(blanking, 0f) - 224f) < 0.001f).IsTrue();
        }
    }

    [Test]
    public async Task PalAtFourTimesSubcarrierLocksToPalGeometry()
    {
        var television = Decode(TelevisionStandard.Pal, 17_734_475, fields: 20);

        await Assert.That(Math.Abs(television.DetectedSamplesPerLine - 1135f) < 1.5f).IsTrue();
        await Assert.That(Math.Abs(television.DetectedLinesPerFrame - 312.5f) < 0.6f).IsTrue();
    }

    [Test]
    public async Task PalAtAMonochromeSourcesNativeRateLocksToPalGeometry()
    {
        var television = Decode(TelevisionStandard.Pal, 13_000_000, fields: 20);

        await Assert.That(Math.Abs(television.DetectedSamplesPerLine - 832f) < 1.5f).IsTrue();
        await Assert.That(Math.Abs(television.DetectedLinesPerFrame - 312.5f) < 0.6f).IsTrue();
    }

    [Test]
    public async Task NtscAtFourTimesSubcarrierStillLocksToNtscGeometry()
    {
        var television = Decode(TelevisionStandard.Ntsc, 14_318_180, fields: 20);

        await Assert.That(Math.Abs(television.DetectedSamplesPerLine - 909.2f) < 1.5f).IsTrue();
        await Assert.That(Math.Abs(television.DetectedLinesPerFrame - 262.5f) < 0.6f).IsTrue();
    }

    [Test]
    public async Task PalActiveVideoRowRangeCoversThePictureLines()
    {
        var television = Decode(TelevisionStandard.Pal, 17_734_475, fields: 20);

        var (_, rowCount) = television.ComputeActiveVideoRowRange();

        // 312.5 lines per field, less the 18-half-line (9 line) vertical sync
        // block. The blank lines after it stay "active": the decoder tells
        // vertical blanking apart by its pulse train, not by picture content.
        await Assert.That(rowCount).IsGreaterThan(290);
        await Assert.That(rowCount).IsLessThan(310);
    }
}
