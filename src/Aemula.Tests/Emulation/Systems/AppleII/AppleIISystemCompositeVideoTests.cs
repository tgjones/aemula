using System.Collections.Generic;
using System.Threading.Tasks;
using Aemula.Emulation.Systems.AppleII;

namespace Aemula.Tests.Emulation.Systems.AppleII;

// Phase 3 of docs/apple-ii-ntsc-video-plan.md: cross-checks the analog
// composite summing-stage formula against the anchor byte values from the
// plan's "Signal representation" table.
public class AppleIISystemCompositeVideoTests
{
    // Same boot budget AppleIISystemVideoModesTests uses - the ROM's boot
    // code sets TEXT mode, so screen-mode soft switches must be poked
    // after boot, not before.
    private static void BootToIdle(AppleIISystem system)
    {
        for (var i = 0; i < 500_000; i++)
        {
            system.Tick();
        }
    }

    private static byte LastSample(AppleIISystem system) =>
        system.CompositeVideo[system.CompositeVideoWriteIndex - 1];

    [Test]
    public async Task SyncTipSamplesAsZero()
    {
        var system = new AppleIISystem();
        system.LoadProgram("");

        var wasPhase0 = system.Phase0;
        byte? sample = null;

        for (var i = 0; i < 2000 && sample is null; i++)
        {
            system.Tick();
            var isPhase0 = system.Phase0;

            if (isPhase0 && !wasPhase0 && system.HSyncPulse)
            {
                sample = LastSample(system);
            }

            wasPhase0 = isPhase0;
        }

        await Assert.That(sample).IsNotNull();
        await Assert.That(sample!.Value).IsEqualTo((byte)0);
    }

    [Test]
    public async Task BlackLevelSamplesAsSixtyFour()
    {
        // Blanked, but neither in the sync pulse nor the burst window -
        // video=0, sync=1, no burst.
        var system = new AppleIISystem();
        system.LoadProgram("");

        byte? sample = null;

        for (var i = 0; i < 2000 && sample is null; i++)
        {
            system.Tick();

            if (system.Hbl && !system.HSyncPulse && !system.ColorBurstGate)
            {
                sample = LastSample(system);
            }
        }

        await Assert.That(sample).IsNotNull();
        await Assert.That(sample!.Value).IsEqualTo((byte)64);
    }

    [Test]
    public async Task WhiteLevelSamplesAsTwoFiftyFive()
    {
        // A genuinely lit HIRES dot during active display - video=1, sync=1.
        var system = new AppleIISystem();
        system.LoadProgram("");

        BootToIdle(system);

        system.WriteByteDebug(0xC050, 0); // GRAPHICS
        system.WriteByteDebug(0xC057, 0); // HIRES
        system.WriteByteDebug(0xC054, 0); // PAGE1

        for (var address = 0x2000; address <= 0x3FFF; address++)
        {
            system.WriteByteDebug((ushort)address, 0b0111_1111);
        }

        byte? sample = null;

        for (var i = 0; i < 400_000 && sample is null; i++)
        {
            system.Tick();

            if (!system.Hbl && !system.Vbl && system.GetVideoDataBitsForTests()[0])
            {
                sample = LastSample(system);
            }
        }

        await Assert.That(sample).IsNotNull();
        await Assert.That(sample!.Value).IsEqualTo((byte)255);
    }

    [Test]
    public async Task ColorBurstSwingsThroughExpectedLevels()
    {
        var system = new AppleIISystem();
        system.LoadProgram("");

        var observed = new HashSet<byte>();

        for (var i = 0; i < 3000; i++)
        {
            system.Tick();

            if (system.ColorBurstGate)
            {
                observed.Add(LastSample(system));
            }
        }

        // Only 4 samples/cycle are achievable at this sample rate (the
        // subcarrier is exactly master/4 - see the plan doc's "Sample
        // rate" section), landing every sample exactly on a zero-crossing
        // or a peak: the black baseline (64), and the two extremes of
        // +/-0.35V around it (byte 19 and 108 - not exactly the
        // Gayler-quoted 13-102, since this formula centers the burst on
        // BlackVoltage (0.5V) rather than Gayler's measured 0.45V center;
        // an accepted small offset, see the plan doc's "Summing formula").
        await Assert.That(observed.Count).IsEqualTo(3);
        await Assert.That(observed.Contains((byte)64)).IsTrue();
        await Assert.That(observed.Contains((byte)19)).IsTrue();
        await Assert.That(observed.Contains((byte)108)).IsTrue();
    }
}
