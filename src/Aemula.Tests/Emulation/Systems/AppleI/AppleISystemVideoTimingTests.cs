using System.Threading.Tasks;
using Aemula.Emulation.Systems.AppleI;

namespace Aemula.Tests.Emulation.Systems.AppleI;

public class AppleISystemVideoTimingTests
{
    // 65 character-times/line * 14 master ticks/character-time - see
    // AppleISystem.VideoTiming.cs.
    private const int MasterTicksPerLine = 65 * 14;

    [Test]
    public async Task HSyncPulsesOnceExactlyOncePerLine()
    {
        var system = new AppleISystem();
        system.LoadProgram("");

        // The counters power up at 0, not at the preset - run past that
        // startup transient (a one-off longer "line") before measuring.
        for (var i = 0; i < MasterTicksPerLine * 2; i++)
        {
            system.Tick();
        }

        var lastHSync = system.HSync;
        var risingEdges = 0;
        var ticksSinceLastEdge = 0;
        var measuredPeriods = new System.Collections.Generic.List<int>();

        for (var i = 0; i < MasterTicksPerLine * 5; i++)
        {
            system.Tick();

            ticksSinceLastEdge++;

            if (system.HSync && !lastHSync)
            {
                risingEdges++;

                if (risingEdges > 1)
                {
                    measuredPeriods.Add(ticksSinceLastEdge);
                }

                ticksSinceLastEdge = 0;
            }

            lastHSync = system.HSync;
        }

        await Assert.That(risingEdges).IsGreaterThanOrEqualTo(4);

        foreach (var period in measuredPeriods)
        {
            await Assert.That(period).IsEqualTo(MasterTicksPerLine);
        }
    }

    [Test]
    public async Task CpuClockDividesMasterOscillatorByFourteen()
    {
        var system = new AppleISystem();
        system.LoadProgram("");

        var lastPhi0 = system.Phi0ForTests;
        var risingEdges = 0;
        var ticksSinceLastEdge = 0;
        var measuredPeriods = new System.Collections.Generic.List<int>();

        for (var i = 0; i < 14 * 20; i++)
        {
            system.Tick();

            ticksSinceLastEdge++;

            if (system.Phi0ForTests && !lastPhi0)
            {
                risingEdges++;

                if (risingEdges > 1)
                {
                    measuredPeriods.Add(ticksSinceLastEdge);
                }

                ticksSinceLastEdge = 0;
            }

            lastPhi0 = system.Phi0ForTests;
        }

        await Assert.That(risingEdges).IsGreaterThanOrEqualTo(10);

        foreach (var period in measuredPeriods)
        {
            await Assert.That(period).IsEqualTo(14);
        }
    }

    [Test]
    public async Task VSyncPulsesOnceEveryTwoHundredFiftySixLines()
    {
        var system = new AppleISystem();
        system.LoadProgram("");

        // Run past the counters' power-up transient before measuring.
        for (var i = 0; i < MasterTicksPerLine * 2; i++)
        {
            system.Tick();
        }

        var lastVSync = system.VSync;
        var risingEdges = 0;
        var ticksSinceLastEdge = 0;
        var measuredPeriods = new System.Collections.Generic.List<int>();

        // A bit over two frames' worth of lines.
        for (var i = 0; i < MasterTicksPerLine * 256 * 2 + MasterTicksPerLine; i++)
        {
            system.Tick();

            ticksSinceLastEdge++;

            if (system.VSync && !lastVSync)
            {
                risingEdges++;

                if (risingEdges > 1)
                {
                    measuredPeriods.Add(ticksSinceLastEdge);
                }

                ticksSinceLastEdge = 0;
            }

            lastVSync = system.VSync;
        }

        await Assert.That(risingEdges).IsGreaterThanOrEqualTo(2);

        foreach (var period in measuredPeriods)
        {
            await Assert.That(period).IsEqualTo(MasterTicksPerLine * 256);
        }
    }
}
