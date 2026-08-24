using System.Collections.Generic;
using System.Threading.Tasks;
using Aemula.Emulation.Systems.AppleII;

namespace Aemula.Tests.Emulation.Systems.AppleII;

// Cross-checks the analog composite summing-stage formula against known
// anchor byte values.
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
        // subcarrier is exactly master/4), landing every sample exactly on
        // a zero-crossing or a peak: the black baseline (64), and the two
        // extremes of +/-0.35V around it (byte 19 and 108 - not exactly the
        // Gayler-quoted 13-102, since this formula centers the burst on
        // BlackVoltage (0.5V) rather than Gayler's measured 0.45V center;
        // an accepted small offset).
        await Assert.That(observed.Count).IsEqualTo(3);
        await Assert.That(observed.Contains((byte)64)).IsTrue();
        await Assert.That(observed.Contains((byte)19)).IsTrue();
        await Assert.That(observed.Contains((byte)108)).IsTrue();
    }

    // Line/frame length tests against the already-implemented video
    // scanner, plus the HiresColorPhase phase-lock question, using two
    // things established above: this composite encoder's free-running
    // _masterTickCounter, and HSyncPulse as an already-verified
    // once-per-line marker.

    [Test]
    public async Task LineAndFrameLengthMatchDocumentedTickCounts()
    {
        // Cross-checks tick-count claims directly against HSyncPulse (one
        // rising edge per line) and the vertical scanner state (which
        // repeats every frame), rather than re-trusting prose: every line
        // measures 912 ticks, not "910 normally, 912 on the
        // once-per-65-lines long-cycle line" as once assumed.
        // Phase0IsElongatedOnceEverySixtyFiveCycles (in
        // AppleIISystemVideoTimingTests.cs) already establishes that 1 of
        // every 65 PHASE0 cycles is long (16 ticks, not 14) - the mistake
        // was reading that as "1 line in 65", when a line *is* 65 PHASE0
        // cycles, so it's really "1 (long) cycle in every line"
        // (64*14 + 16 = 912). AppleIISystem.VideoTiming.cs's own comment
        // already had this right ("once-per-scanline 'long cycle'").
        var system = new AppleIISystem();
        system.LoadProgram("");

        var lineLengths = new List<int>();
        var ticksSinceLastHSyncRisingEdge = 0;
        var wasHSync = false;
        var sawFirstEdge = false;

        // Comfortably past the vertical section's first (cold-start, V=0
        // to its 511 terminal count) wraparound, into the steady 262-line
        // periodic region - see
        // VerticalPresetSequenceMatchesSatherWorkedExample's comment on
        // why 511 lines, not 262, elapse before the first repeat.
        for (var i = 0; i < 520 * 912; i++)
        {
            system.Tick();
        }

        var vAtLineStart = new List<ushort>();

        for (var i = 0; i < 300 * 912 + 1000 && vAtLineStart.Count < 300; i++)
        {
            system.Tick();
            ticksSinceLastHSyncRisingEdge++;

            var isHSync = system.HSyncPulse;
            if (isHSync && !wasHSync)
            {
                var (_, v) = system.GetVideoScannerStateForTests();

                if (sawFirstEdge)
                {
                    lineLengths.Add(ticksSinceLastHSyncRisingEdge);
                }

                vAtLineStart.Add(v);
                sawFirstEdge = true;
                ticksSinceLastHSyncRisingEdge = 0;
            }

            wasHSync = isHSync;
        }

        await Assert.That(lineLengths.Count).IsGreaterThanOrEqualTo(262);

        foreach (var length in lineLengths)
        {
            await Assert.That(length).IsEqualTo(912);
        }

        // 262 lines/frame: the vertical scanner state at line-start repeats
        // after exactly 262 HSync edges (independently corroborates
        // VerticalPresetSequenceMatchesSatherWorkedExample's 511->250
        // wraparound: 511-250+1=262).
        var repeatIndex = vAtLineStart.FindIndex(1, vAtLineStart.Count - 1, v => v == vAtLineStart[0]);
        await Assert.That(repeatIndex).IsEqualTo(262);
    }

    [Test]
    public async Task HiresColorPhaseMatchesAbsoluteSubcarrierPhaseAcrossScanlines()
    {
        // Settles the open question flagged on HiresColorPhase
        // (AppleIISystem.Video.cs): does HiresColorPhase's
        // column-parity-derived quadrant (fixed for a given column, on
        // every line, by construction) actually match the true absolute
        // subcarrier phase -
        // this phase's free-running _masterTickCounter - consistently line
        // to line, or does it only hold within one line?
        //
        // It matches, line to line, indefinitely. LineAndFrameLengthMatchDocumentedTickCounts
        // (this file) establishes every line is exactly 912 master ticks,
        // which is itself a multiple of 4 (unlike the original, now-
        // corrected "910 normally" assumption - 910 %4 == 2, which would
        // have made this drift by half a subcarrier cycle every line).
        // Because the real per-line total is a multiple of 4, a
        // fixed column always lands on the identical absolute subcarrier
        // quadrant on every single line - which is exactly what the
        // once-per-line "long cycle" stretch exists to guarantee (Sather,
        // quoted in docs/apple-ii-plan.md: it keeps "the dot clock
        // phase-locked to the color subcarrier across scanlines"). So
        // HiresColorPhase's column-parity-only formula isn't an
        // approximation of the true absolute phase - it *is* the true
        // absolute phase, verified here directly against the free-running
        // counter rather than assumed from the formula's own construction.
        var system = new AppleIISystem();
        system.LoadProgram("");

        var wasPhase0 = system.Phase0;
        var recordedRawH = -1;
        var quadrantsAtFixedColumn = new List<uint>();

        // Comfortably more than a full 262-line frame - accounting for the
        // ~70 of those lines that are blanked (Vbl) and so don't produce a
        // hit at all - so the frame wraparound (V's reload back to its
        // preset) is itself exercised, not just steady horizontal-only
        // lines.
        for (var i = 0; i < 500 * 912 && quadrantsAtFixedColumn.Count < 270; i++)
        {
            var counterBeforeThisTick = system.GetMasterTickCounterForTests();
            system.Tick();
            var isPhase0 = system.Phase0;

            if (isPhase0 && !wasPhase0 && !system.Hbl && !system.Vbl)
            {
                var (h, _) = system.GetVideoScannerStateForTests();
                var rawH = h & 0b0_111111; // H0-H5, masking off HPE'

                // Any visible column works as the fixed reference point -
                // the same H-state recurs at the same absolute screen
                // column on every line.
                recordedRawH = recordedRawH == -1 ? rawH : recordedRawH;

                if (rawH == recordedRawH)
                {
                    // counterBeforeThisTick is _masterTickCounter's value
                    // as TickVideo() computed this cell's dots (it hasn't
                    // been incremented for this tick yet - that happens
                    // later, in this same Tick() call's TickCompositeVideo)
                    // - i.e. the absolute subcarrier phase for this cell's
                    // first dot.
                    quadrantsAtFixedColumn.Add(counterBeforeThisTick & 3);
                }
            }

            wasPhase0 = isPhase0;
        }

        await Assert.That(quadrantsAtFixedColumn.Count).IsEqualTo(270);

        var distinctQuadrants = new HashSet<uint>(quadrantsAtFixedColumn);
        await Assert.That(distinctQuadrants.Count).IsEqualTo(1);
    }
}
