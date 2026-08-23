using System.Collections.Generic;
using System.Threading.Tasks;
using Aemula.Emulation.Output.Ntsc;
using Aemula.Emulation.Systems.AppleII;

namespace Aemula.Tests.Emulation.Output.Ntsc;

public class NtscRasterOscillatorsTests
{
    private static (NtscSyncSeparator Separator, NtscRasterOscillators Oscillators) RunPipeline(IReadOnlyList<byte> samples)
    {
        var separator = new NtscSyncSeparator();
        var oscillators = new NtscRasterOscillators();

        for (var i = 0; i < samples.Count; i++)
        {
            separator.Process(samples[i]);
            oscillators.Process(separator.HSyncDetected, separator.VSyncDetected);
        }

        return (separator, oscillators);
    }

    [Test]
    public async Task LocksToSmpteAssetTiming()
    {
        var samples = SmpteAsset.LoadNormalized();
        var (_, oscillators) = RunPipeline(samples);

        // smpte.ntsc's own byte count implies 910 samples/line, 262.5
        // lines/field (see docs/television-plan.md's "Existing state") -
        // measured here, not asserted exactly, since these are running
        // estimates over a real (if synthetic) signal, not configuration.
        await Assert.That(oscillators.DetectedSamplesPerLine).IsBetween(905.0f, 915.0f);
        await Assert.That(oscillators.DetectedLinesPerFrame).IsBetween(260.0f, 265.0f);
    }

    [Test]
    public async Task LocksToAppleIICompositeVideoTiming()
    {
        var system = new AppleIISystem();
        system.LoadProgram("");

        // Enough ticks to wrap the composite-video ring buffer at least
        // once, so every slot holds real steady-state signal (matching
        // AppleIISystemCompositeVideoTests' own boot budget).
        for (var i = 0; i < 600_000; i++)
        {
            system.Tick();
        }

        // The ring buffer (CompositeVideoCapacity = 262*912 samples) always
        // holds exactly one field's worth of samples, so concatenating it
        // with itself gives the vertical oscillator a genuine field
        // boundary to measure the spacing across, the same way it would
        // see one in a longer live stream.
        var oneField = system.CompositeVideo;
        var samples = new byte[oneField.Length * 2];
        oneField.CopyTo(samples, 0);
        oneField.CopyTo(samples, oneField.Length);

        var (_, oscillators) = RunPipeline(samples);

        // Apple II's own timing (see AppleIISystem.CompositeVideo.cs) is
        // 912 samples/line, 262 lines/field.
        await Assert.That(oscillators.DetectedSamplesPerLine).IsBetween(907.0f, 917.0f);
        await Assert.That(oscillators.DetectedLinesPerFrame).IsBetween(260.0f, 264.0f);
    }

    [Test]
    public async Task FailsToLockOntoOutOfRangeSyncTiming()
    {
        // A bogus "sync" pulse train at a period nowhere near any real
        // NTSC-family line length (~909 samples): far too short to be
        // mistaken for a normal line, and comfortably outside
        // NtscRasterOscillators' bounded 20% max-drift range around
        // nominal, so no number of accepted pulses can ever drag the
        // estimate anywhere near it. The pulse width itself (60 samples) is
        // kept within NtscSyncSeparator's normal HSYNC-width tolerance
        // band, so these pulses really do reach NtscRasterOscillators as
        // HSyncDetected candidates - it's specifically the oscillators'
        // own capture-range/drift-clamp logic under test here, not
        // whether the separator notices the pulses at all.
        const byte syncSample = 0;
        const byte highSample = 150;
        const int bogusPeriod = 300;
        const int pulseWidth = 60;

        var samples = new List<byte>();
        for (var pulse = 0; pulse < 40; pulse++)
        {
            for (var i = 0; i < bogusPeriod - pulseWidth; i++) samples.Add(highSample);
            for (var i = 0; i < pulseWidth; i++) samples.Add(syncSample);
        }

        var (_, oscillators) = RunPipeline(samples);

        // Should have stayed close to the nominal default throughout,
        // rather than drifting toward the bogus 300-sample rate.
        await Assert.That(oscillators.DetectedSamplesPerLine).IsBetween(850.0f, 970.0f);
    }
}
