using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl74163ChipTests
{
    private static void Pulse(Ttl74163Chip chip)
    {
        chip.Clk = true;
        chip.Clk = false;
    }

    private static int Count(Ttl74163Chip chip) =>
        (chip.Qa ? 1 : 0) | (chip.Qb ? 2 : 0) | (chip.Qc ? 4 : 0) | (chip.Qd ? 8 : 0);

    [Test]
    public async Task CountsOnRisingEdgeAndWraps()
    {
        var chip = new Ttl74163Chip { Enp = true, Ent = true };

        for (var i = 0; i < 5; i++)
        {
            Pulse(chip);
        }
        await Assert.That(Count(chip)).IsEqualTo(5);

        for (var i = 0; i < 11; i++)
        {
            Pulse(chip);
        }
        await Assert.That(Count(chip)).IsEqualTo(0);
    }

    [Test]
    public async Task HoldsWhenEitherEnableLow()
    {
        var chip = new Ttl74163Chip { Enp = true, Ent = true };
        Pulse(chip);

        chip.Enp = false;
        Pulse(chip);
        chip.Enp = true;
        chip.Ent = false;
        Pulse(chip);

        await Assert.That(Count(chip)).IsEqualTo(1);
    }

    [Test]
    public async Task ClearIsSynchronous()
    {
        var chip = new Ttl74163Chip { Enp = true, Ent = true };
        Pulse(chip);
        Pulse(chip);

        chip.Clr = false;
        await Assert.That(Count(chip)).IsEqualTo(2);

        Pulse(chip);
        await Assert.That(Count(chip)).IsEqualTo(0);
    }

    [Test]
    public async Task ClearOverridesLoad()
    {
        var chip = new Ttl74163Chip { A = true, B = true, Load = false, Clr = false };
        Pulse(chip);
        await Assert.That(Count(chip)).IsEqualTo(0);
    }

    [Test]
    public async Task LoadIsSynchronousAndOverridesCount()
    {
        var chip = new Ttl74163Chip { A = true, C = true, Enp = true, Ent = true };

        chip.Load = false;
        await Assert.That(Count(chip)).IsEqualTo(0);

        Pulse(chip);
        await Assert.That(Count(chip)).IsEqualTo(5);
    }

    [Test]
    public async Task RcoHighOnlyAtFifteenWithEntAsserted()
    {
        var chip = new Ttl74163Chip { A = true, B = true, C = true, D = true, Load = false, Ent = true };
        Pulse(chip);
        chip.Load = true;

        await Assert.That(chip.Rco).IsEqualTo(true);

        chip.Ent = false;
        await Assert.That(chip.Rco).IsEqualTo(false);
    }
}
