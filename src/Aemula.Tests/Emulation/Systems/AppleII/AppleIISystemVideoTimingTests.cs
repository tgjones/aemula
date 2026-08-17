using System.Collections.Generic;
using System.Threading.Tasks;
using Aemula.Emulation.Systems.AppleII;

namespace Aemula.Tests.Emulation.Systems.AppleII;

// Cross-checks the phase 3 video scanner / clock generator against the
// exact worked examples and frequencies given in Jim Sather's "Understanding
// the Apple II", chapter 3.
public class AppleIISystemVideoTimingTests
{
    private static List<(byte H, ushort V)> CollectDistinctScannerStates(AppleIISystem system, int maxTicks, int maxStates)
    {
        var states = new List<(byte H, ushort V)> { system.GetVideoScannerStateForTests() };

        for (var i = 0; i < maxTicks && states.Count < maxStates; i++)
        {
            system.Tick();

            var current = system.GetVideoScannerStateForTests();
            if (current != states[^1])
            {
                states.Add(current);
            }
        }

        return states;
    }

    private static int FindHorizontalState(List<(byte H, ushort V)> states, byte h, int startIndex)
    {
        for (var i = startIndex; i < states.Count; i++)
        {
            if (states[i].H == h)
            {
                return i;
            }
        }

        return -1;
    }

    [Test]
    public async Task HorizontalDoubleZeroStateMatchesSatherWorkedExample()
    {
        // "A typical count sequence is 111100000/1111111; 111100001/0000000;
        // 111100001/1000000." (Sather, p.3-15) - the horizontal section's
        // once-per-line double-zero state, with VA carrying in and then
        // holding through the reload.
        var system = new AppleIISystem();
        system.LoadProgram("");

        var states = CollectDistinctScannerStates(system, maxTicks: 5000, maxStates: 2000);

        var maxIndex = FindHorizontalState(states, 0b1111111, 0);
        await Assert.That(maxIndex).IsGreaterThanOrEqualTo(0);
        await Assert.That(states.Count).IsGreaterThan(maxIndex + 2);

        var (hMax, vMax) = states[maxIndex];
        var (hZero1, vZero1) = states[maxIndex + 1];
        var (hZero2, vZero2) = states[maxIndex + 2];

        await Assert.That(hMax).IsEqualTo((byte)0b1111111);
        await Assert.That(hZero1).IsEqualTo((byte)0b0000000);
        await Assert.That(hZero2).IsEqualTo((byte)0b1000000);

        // VA (bit 0 of V) carries in going from the max state to the first
        // zero state, then holds through the second (reload) zero state.
        await Assert.That(vZero1).IsEqualTo((ushort)(vMax | 1));
        await Assert.That(vZero2).IsEqualTo(vZero1);
    }

    [Test]
    public async Task VerticalPresetSequenceMatchesSatherWorkedExample()
    {
        // "The vertical preset sequence is 111111111/1111111;
        // 011111010/0000000; 011111010/1000000." (Sather, p.3-15) - once per
        // frame, the vertical section reloads from its terminal count.
        var system = new AppleIISystem();
        system.LoadProgram("");

        // Starting cold at V=0, the vertical section's terminal count
        // (511, not the steady-state modulus of 262) is first reached 511
        // lines in; budget generously in master ticks for that.
        var states = CollectDistinctScannerStates(system, maxTicks: 600_000, maxStates: 60_000);

        var index = 0;
        var found = false;
        (byte H, ushort V) atMax = default, atZero1 = default, atZero2 = default;

        while (true)
        {
            index = FindHorizontalState(states, 0b1111111, index);
            if (index < 0 || index + 2 >= states.Count)
            {
                break;
            }

            atMax = states[index];
            atZero1 = states[index + 1];
            atZero2 = states[index + 2];

            if (atMax.V == 0b1_1111_1111)
            {
                found = true;
                break;
            }

            index++;
        }

        await Assert.That(found).IsTrue();
        await Assert.That(atZero1.H).IsEqualTo((byte)0b0000000);
        await Assert.That(atZero1.V).IsEqualTo((ushort)0b0_1111_1010);
        await Assert.That(atZero2.H).IsEqualTo((byte)0b1000000);
        await Assert.That(atZero2.V).IsEqualTo(atZero1.V);
    }

    [Test]
    public async Task Phase0IsElongatedOnceEverySixtyFiveCycles()
    {
        // 64 out of every 65 CPU cycles are 14 master ticks (normal); 1 is
        // 16 ticks (the "long cycle"), keeping the dot clock phase-locked
        // to the color subcarrier across scanlines.
        var system = new AppleIISystem();
        system.LoadProgram("");

        var cycleLengths = new List<int>();
        var ticksSinceLastRisingEdge = 0;
        var sawFirstEdge = false;

        for (var i = 0; i < 2000 && cycleLengths.Count < 130; i++)
        {
            var wasPhase0 = system.Phase0;
            system.Tick();
            ticksSinceLastRisingEdge++;

            if (!wasPhase0 && system.Phase0)
            {
                if (sawFirstEdge)
                {
                    cycleLengths.Add(ticksSinceLastRisingEdge);
                }

                sawFirstEdge = true;
                ticksSinceLastRisingEdge = 0;
            }
        }

        await Assert.That(cycleLengths.Count).IsGreaterThanOrEqualTo(65);

        var normalCount = 0;
        var longCount = 0;
        foreach (var length in cycleLengths)
        {
            if (length == 14)
            {
                normalCount++;
            }
            else if (length == 16)
            {
                longCount++;
            }
        }

        await Assert.That(normalCount + longCount).IsEqualTo(cycleLengths.Count);
        await Assert.That(longCount).IsGreaterThanOrEqualTo(1);

        // Exactly 1 long cycle per 65 - ratio should match within the sample.
        var expectedLongCycles = cycleLengths.Count / 65;
        await Assert.That(longCount).IsEqualTo(expectedLongCycles);
    }

    [Test]
    public async Task HSyncPulseIsFourHCountsImmediatelyBeforeColorBurstGate()
    {
        // docs/apple-ii-ntsc-video-plan.md, "Composite sync": HSync is a
        // 4-H-count pulse, immediately followed by the already-implemented
        // ColorBurstGate window.
        var system = new AppleIISystem();
        system.LoadProgram("");

        var hsyncStates = new List<bool>();
        var burstStates = new List<bool>();
        var wasPhase0 = system.Phase0;

        for (var i = 0; i < 2000; i++)
        {
            system.Tick();
            var isPhase0 = system.Phase0;

            if (isPhase0 && !wasPhase0)
            {
                hsyncStates.Add(system.HSyncPulse);
                burstStates.Add(system.ColorBurstGate);
            }

            wasPhase0 = isPhase0;
        }

        var start = hsyncStates.FindIndex(v => v);
        await Assert.That(start).IsGreaterThanOrEqualTo(0);

        var end = start;
        while (end < hsyncStates.Count && hsyncStates[end])
        {
            end++;
        }

        await Assert.That(end - start).IsEqualTo(4);
        await Assert.That(burstStates[end]).IsTrue();

        for (var i = start; i < end; i++)
        {
            await Assert.That(burstStates[i]).IsFalse();
        }
    }

    [Test]
    public async Task HSyncAndVSyncPulsesMatchDocumentedEquations()
    {
        // docs/apple-ii-ntsc-video-plan.md, "Composite sync": cross-checks
        // HSyncPulse/VSyncPulse against the documented boolean equations
        // (RFI-revision: vertical serration term (H5+H4+H3)) independently
        // re-derived from the packed scanner state, across a real run long
        // enough to pass through the vertical sync region (V=480-483, ~480
        // lines from a cold reset).
        var system = new AppleIISystem();
        system.LoadProgram("");

        var wasPhase0 = system.Phase0;
        var sawHSyncTrue = false;
        var sawVSyncTrue = false;
        var mismatches = 0;

        for (var i = 0; i < 560_000; i++)
        {
            system.Tick();
            var isPhase0 = system.Phase0;

            if (isPhase0 && !wasPhase0)
            {
                var (h, v) = system.GetVideoScannerStateForTests();

                var h5 = (h & 0b0_100000) != 0;
                var h4 = (h & 0b0_010000) != 0;
                var h3 = (h & 0b0_001000) != 0;
                var h2 = (h & 0b0_000100) != 0;

                var v4 = (v & 0b0_1000_0000) != 0;
                var v3 = (v & 0b0_0100_0000) != 0;
                var v2 = (v & 0b0_0010_0000) != 0;
                var v1 = (v & 0b0_0001_0000) != 0;
                var v0 = (v & 0b0_0000_1000) != 0;
                var vc = (v & 0b0_0000_0100) != 0;

                var expectedHSync = !h5 && !h4 && h3 && !h2;
                var expectedVSync = v4 && v3 && v2 && !v1 && !v0 && !vc && (h5 || h4 || h3);

                if (system.HSyncPulse != expectedHSync || system.VSyncPulse != expectedVSync)
                {
                    mismatches++;
                }

                sawHSyncTrue |= expectedHSync;
                sawVSyncTrue |= expectedVSync;
            }

            wasPhase0 = isPhase0;
        }

        // Sanity checks that the equality checks above weren't vacuous.
        await Assert.That(sawHSyncTrue).IsTrue();
        await Assert.That(sawVSyncTrue).IsTrue();

        await Assert.That(mismatches).IsEqualTo(0);
    }
}
