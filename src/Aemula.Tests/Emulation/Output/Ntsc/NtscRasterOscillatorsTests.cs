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

    // Regression test for a real deadlock found against a real Atari2600
    // ROM (Pitfall): one anomalous pulse - shaped like neither a clean
    // HSYNC nor a clean VSYNC, produced by TIA's own not-yet-settled
    // horizontal-counter state very early in a real boot - offset
    // PullInOscillator's _samplesSinceAccepted by a full extra period
    // relative to every following genuine pulse. Since that counter only
    // ever resets on an accept, and every later pulse's capture-range
    // check is measured against it, this was a *permanent* deadlock, not
    // a temporary loss of lock - DetectedSamplesPerLine stayed frozen at
    // the nominal default for the rest of the run (60M+ ticks observed),
    // even though the real signal's own line length (912 samples,
    // measured directly against TIA's own pins) never wavered.
    //
    // Modelled here as the simplest version of that same shape: several
    // genuine pulses at a period close to nominal to lock on normally,
    // one full period's worth of missing pulse (standing in for the one
    // real pulse that didn't classify as HSYNC), then many more genuine
    // pulses at a *different*, legitimately-reachable period (just
    // outside the single-pulse capture range from where the oscillator
    // was already locked, but within the max-drift band) - deliberately
    // different from the pre-gap period, not the same one, so a run that
    // stays stuck at the old lock and a run that genuinely recovers don't
    // land on the same number by coincidence.
    [Test]
    public async Task RecoversLockAfterOneAnomalousGapPermanentlyDesyncsCapture()
    {
        const byte syncSample = 0;
        const byte highSample = 150;
        const int preGapPeriod = 910; // close to nominal (~909.3) - locks on trivially.
        const int postGapPeriod = 1050; // ~15.4% above preGapPeriod - just past the single-pulse 15% capture range, but within the 20% max-drift band.
        const int pulseWidth = 64; // Atari2600's own real HSYNC pulse width (16 OSC ticks * 4 sub-samples) - used for both, this test isn't exercising pulse-width classification.

        var samples = new List<byte>();

        void AddPulse(int period)
        {
            for (var i = 0; i < period - pulseWidth; i++) samples.Add(highSample);
            for (var i = 0; i < pulseWidth; i++) samples.Add(syncSample);
        }

        // Lock on normally first.
        for (var i = 0; i < 5; i++)
        {
            AddPulse(preGapPeriod);
        }

        // One anomalous gap - see this test's own remarks above.
        for (var i = 0; i < preGapPeriod; i++)
        {
            samples.Add(highSample);
        }

        // Resume with a real, still-periodic signal at a different period -
        // permanently out of phase (and now off-frequency) relative to the
        // oscillator's last accepted point.
        for (var i = 0; i < 40; i++)
        {
            AddPulse(postGapPeriod);
        }

        var (_, oscillators) = RunPipeline(samples);

        // Without re-acquisition, this stays stuck at (or near) the
        // pre-gap lock forever, since every post-gap pulse's
        // _samplesSinceAccepted lands outside the 15% capture window
        // around it, with no way for that counter to ever reset again.
        await Assert.That(oscillators.DetectedSamplesPerLine).IsBetween(1030.0f, 1070.0f);
    }
}
