using System.Collections.Generic;
using System.Threading.Tasks;
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
}
