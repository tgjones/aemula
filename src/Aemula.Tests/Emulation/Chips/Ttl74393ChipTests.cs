using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl74393ChipTests
{
    private static void Pulse1(Ttl74393Chip chip)
    {
        chip.Clk1 = true;
        chip.Clk1 = false;
    }

    private static int Count1(Ttl74393Chip chip) =>
        (chip.Qa1 ? 1 : 0) | (chip.Qb1 ? 2 : 0) | (chip.Qc1 ? 4 : 0) | (chip.Qd1 ? 8 : 0);

    [Test]
    public async Task CountsOnFallingEdge()
    {
        var chip = new Ttl74393Chip();

        chip.Clk1 = true;
        await Assert.That(Count1(chip)).IsEqualTo(0);

        chip.Clk1 = false;
        await Assert.That(Count1(chip)).IsEqualTo(1);
    }

    [Test]
    public async Task CountsThroughAndWrapsAtSixteen()
    {
        var chip = new Ttl74393Chip();

        for (var i = 0; i < 11; i++)
        {
            Pulse1(chip);
        }
        await Assert.That(Count1(chip)).IsEqualTo(11);

        for (var i = 0; i < 5; i++)
        {
            Pulse1(chip);
        }
        await Assert.That(Count1(chip)).IsEqualTo(0);
    }

    [Test]
    public async Task ClearIsAsynchronousAndActiveHigh()
    {
        var chip = new Ttl74393Chip();
        Pulse1(chip);
        Pulse1(chip);

        chip.Clr1 = true;
        await Assert.That(Count1(chip)).IsEqualTo(0);

        Pulse1(chip);
        await Assert.That(Count1(chip)).IsEqualTo(0);

        chip.Clr1 = false;
        Pulse1(chip);
        await Assert.That(Count1(chip)).IsEqualTo(1);
    }

    [Test]
    public async Task CountersAreIndependent()
    {
        var chip = new Ttl74393Chip();
        Pulse1(chip);
        Pulse1(chip);
        Pulse1(chip);
        chip.Clk2 = true;
        chip.Clk2 = false;

        await Assert.That(Count1(chip)).IsEqualTo(3);
        await Assert.That(chip.Qa2).IsEqualTo(true);
        await Assert.That(chip.Qb2).IsEqualTo(false);

        chip.Clr2 = true;
        await Assert.That(chip.Qa2).IsEqualTo(false);
        await Assert.That(Count1(chip)).IsEqualTo(3);
    }
}
