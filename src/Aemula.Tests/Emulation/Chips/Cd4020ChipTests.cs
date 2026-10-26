using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Cd4020ChipTests
{
    private static void Pulse(Cd4020Chip chip, int times = 1)
    {
        for (var i = 0; i < times; i++)
        {
            chip.Clk = true;
            chip.Clk = false;
        }
    }

    [Test]
    public async Task CountsOnFallingEdge()
    {
        var chip = new Cd4020Chip();

        chip.Clk = true;
        await Assert.That(chip.Q1).IsEqualTo(false);

        chip.Clk = false;
        await Assert.That(chip.Q1).IsEqualTo(true);
    }

    [Test]
    public async Task OutputsAreTheMatchingCountBits()
    {
        var chip = new Cd4020Chip();
        Pulse(chip, 8);

        await Assert.That(chip.Q1).IsEqualTo(false);
        await Assert.That(chip.Q4).IsEqualTo(true);
        await Assert.That(chip.Q5).IsEqualTo(false);

        Pulse(chip, 8);
        await Assert.That(chip.Q4).IsEqualTo(false);
        await Assert.That(chip.Q5).IsEqualTo(true);
    }

    [Test]
    public async Task HighestStageToggles()
    {
        var chip = new Cd4020Chip();
        Pulse(chip, 8192);
        await Assert.That(chip.Q14).IsEqualTo(true);
        await Assert.That(chip.Q13).IsEqualTo(false);

        Pulse(chip, 8192);
        await Assert.That(chip.Q14).IsEqualTo(false);
    }

    [Test]
    public async Task ResetClearsAndHoldsAtZero()
    {
        var chip = new Cd4020Chip();
        Pulse(chip, 5);
        await Assert.That(chip.Q1).IsEqualTo(true);

        chip.Reset = true;
        await Assert.That(chip.Q1).IsEqualTo(false);
        await Assert.That(chip.Q4).IsEqualTo(false);

        Pulse(chip, 3);
        await Assert.That(chip.Q1).IsEqualTo(false);

        chip.Reset = false;
        Pulse(chip);
        await Assert.That(chip.Q1).IsEqualTo(true);
    }
}
