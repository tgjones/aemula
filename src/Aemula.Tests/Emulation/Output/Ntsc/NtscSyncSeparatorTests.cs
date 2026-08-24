using System.Collections.Generic;
using System.Threading.Tasks;
using Aemula.Emulation.Output;
using Aemula.Emulation.Output.Ntsc;

namespace Aemula.Tests.Emulation.Output.Ntsc;

public class NtscSyncSeparatorTests
{
    // Comfortably below the seeded sync/black midpoint threshold (see
    // NtscSyncSeparator's InitialSyncLevel/InitialBlackLevel) and
    // comfortably above it, respectively - stand-ins for "sync tip" and
    // "picture" samples that don't need to represent any particular real
    // voltage for these tests.
    private const byte SyncSample = 0;
    private const byte HighSample = 150;

    [Test]
    public async Task DetectsHSyncAtEachPulseTrailingEdge()
    {
        const int lineLength = 200;
        const int hsyncWidth = 67; // matches NtscSyncSeparator's ~67.3-sample nominal HSYNC width

        var samples = new List<byte>();
        var expectedHSyncIndices = new List<int>();

        for (var line = 0; line < 4; line++)
        {
            for (var i = 0; i < lineLength - hsyncWidth; i++)
            {
                samples.Add(HighSample);
            }

            for (var i = 0; i < hsyncWidth; i++)
            {
                samples.Add(SyncSample);
            }

            // The trailing edge fires on the very next sample after the low
            // run ends - which, at this point in building the array, is
            // exactly the sample about to be appended (index == current
            // count).
            expectedHSyncIndices.Add(samples.Count);
        }

        // The last line's pulse needs one more high sample after it for its
        // trailing edge to actually fire against.
        samples.Add(HighSample);

        var separator = new NtscSyncSeparator();
        var actualHSyncIndices = new List<int>();

        for (var i = 0; i < samples.Count; i++)
        {
            separator.Process(samples[i]);

            if (separator.HSyncDetected)
            {
                actualHSyncIndices.Add(i);
            }
        }

        await Assert.That(actualHSyncIndices).IsEquivalentTo(expectedHSyncIndices);
    }

    [Test]
    public async Task DetectsVSyncOnLongLowRunButNotHSync()
    {
        const int highSegmentLength = 100;

        // ~5.8x a normal HSYNC pulse, matching the real ~27.1µs broad
        // vertical sync pulse vs. ~4.7µs HSYNC ratio - comfortably past
        // NtscSyncSeparator's 3x-of-current-HSYNC-estimate VSYNC threshold.
        // This test approximates the real serrated vertical-sync waveform
        // as one uniform long low run, since only pulse-width
        // classification is under test here, not serration shape.
        const int vsyncWidth = 400;

        var samples = new List<byte>();

        for (var i = 0; i < highSegmentLength; i++) samples.Add(HighSample);
        for (var i = 0; i < vsyncWidth; i++) samples.Add(SyncSample);
        for (var i = 0; i < highSegmentLength; i++) samples.Add(HighSample);

        var separator = new NtscSyncSeparator();

        var hsyncEverFired = false;
        var vsyncIndex = -1;

        for (var i = 0; i < samples.Count; i++)
        {
            separator.Process(samples[i]);

            if (separator.HSyncDetected)
            {
                hsyncEverFired = true;
            }

            if (separator.VSyncDetected)
            {
                vsyncIndex = i;
            }
        }

        await Assert.That(hsyncEverFired).IsFalse();
        await Assert.That(vsyncIndex).IsEqualTo(highSegmentLength + vsyncWidth);
    }

    // CurrentSyncRegion: unlike HSyncDetected/VSyncDetected (which only fire
    // once, on the sample where a completed pulse's trailing edge is
    // found), this is live for every sample of an in-progress pulse - see
    // this property's own remarks.
    [Test]
    public async Task CurrentSyncRegionIsHSyncThroughoutANormalPulseAndNullOutsideIt()
    {
        const int highSegmentLength = 50;
        const int hsyncWidth = 67; // matches NtscSyncSeparator's ~67.3-sample nominal HSYNC width
        const int confirmSamples = 2; // matches NtscSyncSeparator's own LiveSyncRegionConfirmSamples

        var samples = new List<byte>();
        for (var i = 0; i < highSegmentLength; i++) samples.Add(HighSample);
        for (var i = 0; i < hsyncWidth; i++) samples.Add(SyncSample);
        for (var i = 0; i < highSegmentLength; i++) samples.Add(HighSample);

        var separator = new NtscSyncSeparator();

        for (var i = 0; i < samples.Count; i++)
        {
            separator.Process(samples[i]);

            // The first couple of samples of even a genuine pulse stay null
            // rather than immediately HSync - see LiveSyncRegionConfirmSamples'
            // remarks on why an unconfirmed single-sample-or-two dip can't
            // yet be told apart from noise (color burst's own negative
            // half-cycle being the recurring real-signal example).
            var confirmedWithinPulse = i >= highSegmentLength + confirmSamples - 1
                && i < highSegmentLength + hsyncWidth;

            await Assert.That(separator.CurrentSyncRegion)
                .IsEqualTo(confirmedWithinPulse ? RasterRegion.HSync : null);
        }
    }

    [Test]
    public async Task CurrentSyncRegionFlipsToVSyncLiveOnceAnInProgressPulseGrowsPastTheThreshold()
    {
        const int highSegmentLength = 50;

        // Comfortably past 3x the ~67.3-sample nominal HSYNC width (the
        // same VSYNC-width threshold ClassifyCompletedLowRun uses), so this
        // pulse's classification genuinely flips mid-pulse, not just at its
        // very end.
        const int lowRunLength = 250;

        var samples = new List<byte>();
        for (var i = 0; i < highSegmentLength; i++) samples.Add(HighSample);
        for (var i = 0; i < lowRunLength; i++) samples.Add(SyncSample);
        for (var i = 0; i < highSegmentLength; i++) samples.Add(HighSample);

        var separator = new NtscSyncSeparator();

        RasterRegion? earlyRegion = null;
        RasterRegion? lateRegion = null;

        for (var i = 0; i < samples.Count; i++)
        {
            separator.Process(samples[i]);

            // A handful of samples into the pulse - still well below the
            // VSYNC threshold, so this should read as a (tentative, not yet
            // reclassified) HSYNC - the pulse looks like a normal one so far.
            if (i == highSegmentLength + 10)
            {
                earlyRegion = separator.CurrentSyncRegion;
            }

            // Comfortably past the threshold, while the pulse is still
            // ongoing (this sample is not the pulse's trailing edge).
            if (i == highSegmentLength + lowRunLength - 10)
            {
                lateRegion = separator.CurrentSyncRegion;
            }
        }

        await Assert.That(earlyRegion).IsEqualTo(RasterRegion.HSync);
        await Assert.That(lateRegion).IsEqualTo(RasterRegion.VSync);
    }
}
