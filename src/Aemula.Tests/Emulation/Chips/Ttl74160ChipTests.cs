using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl74160ChipTests
{
    private static void Pulse(Ttl74160Chip chip)
    {
        chip.Clk = true;
        chip.Clk = false;
    }

    [Test]
    public async Task CountsUpOnRisingEdgeWhenEnabled()
    {
        var chip = new Ttl74160Chip { Enp = true, Ent = true };

        Pulse(chip);

        await Assert.That(chip.Qa).IsEqualTo(true);
        await Assert.That(chip.Qb).IsEqualTo(false);
        await Assert.That(chip.Qc).IsEqualTo(false);
        await Assert.That(chip.Qd).IsEqualTo(false);
    }

    [Test]
    public async Task CountsThroughFullSequence()
    {
        var chip = new Ttl74160Chip { Enp = true, Ent = true };

        for (var i = 0; i < 5; i++)
        {
            Pulse(chip);
        }

        var count = (chip.Qa ? 1 : 0) | (chip.Qb ? 2 : 0) | (chip.Qc ? 4 : 0) | (chip.Qd ? 8 : 0);
        await Assert.That(count).IsEqualTo(5);
    }

    [Test]
    public async Task WrapsFromNineToZero()
    {
        var chip = new Ttl74160Chip { Enp = true, Ent = true };

        for (var i = 0; i < 10; i++)
        {
            Pulse(chip);
        }

        await Assert.That(chip.Qa).IsEqualTo(false);
        await Assert.That(chip.Qb).IsEqualTo(false);
        await Assert.That(chip.Qc).IsEqualTo(false);
        await Assert.That(chip.Qd).IsEqualTo(false);
    }

    [Test]
    public async Task HoldsWhenEnpIsLow()
    {
        var chip = new Ttl74160Chip { Enp = false, Ent = true };

        Pulse(chip);

        await Assert.That(chip.Qa).IsEqualTo(false);
    }

    [Test]
    public async Task HoldsWhenEntIsLow()
    {
        var chip = new Ttl74160Chip { Enp = true, Ent = false };

        Pulse(chip);

        await Assert.That(chip.Qa).IsEqualTo(false);
    }

    [Test]
    public async Task LoadsParallelDataOnRisingEdge()
    {
        var chip = new Ttl74160Chip
        {
            A = true,
            B = false,
            C = true,
            D = false,
            Load = false,
        };

        Pulse(chip);

        await Assert.That(chip.Qa).IsEqualTo(true);
        await Assert.That(chip.Qb).IsEqualTo(false);
        await Assert.That(chip.Qc).IsEqualTo(true);
        await Assert.That(chip.Qd).IsEqualTo(false);
    }

    [Test]
    public async Task LoadTakesPriorityOverCounting()
    {
        var chip = new Ttl74160Chip
        {
            A = true,
            Enp = true,
            Ent = true,
            Load = false,
        };

        Pulse(chip);

        await Assert.That(chip.Qa).IsEqualTo(true);
        await Assert.That(chip.Qb).IsEqualTo(false);
    }

    [Test]
    public async Task AsyncClearForcesOutputsLowRegardlessOfClock()
    {
        var chip = new Ttl74160Chip { Enp = true, Ent = true };
        Pulse(chip);

        chip.Clr = false;

        await Assert.That(chip.Qa).IsEqualTo(false);
    }

    [Test]
    public async Task ClockIsIgnoredWhileClearIsAsserted()
    {
        var chip = new Ttl74160Chip { Enp = true, Ent = true, Clr = false };

        Pulse(chip);

        await Assert.That(chip.Qa).IsEqualTo(false);
    }

    [Test]
    public async Task RcoIsHighOnlyAtMaxCountWithEntHigh()
    {
        var chip = new Ttl74160Chip { Enp = true, Ent = true };

        for (var i = 0; i < 9; i++)
        {
            Pulse(chip);
        }

        await Assert.That(chip.Rco).IsEqualTo(true);
    }

    [Test]
    public async Task RcoIsLowAtMaxCountWhenEntIsLow()
    {
        var chip = new Ttl74160Chip { Enp = true, Ent = true };

        for (var i = 0; i < 9; i++)
        {
            Pulse(chip);
        }

        chip.Ent = false;

        await Assert.That(chip.Rco).IsEqualTo(false);
    }

    [Test]
    public async Task RcoIsLowBelowMaxCount()
    {
        var chip = new Ttl74160Chip { Enp = true, Ent = true };

        Pulse(chip);

        await Assert.That(chip.Rco).IsEqualTo(false);
    }
}
