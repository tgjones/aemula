using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Cd4013ChipTests
{
    [Test]
    public async Task CapturesDataOnRisingEdge()
    {
        var chip = new Cd4013Chip { D1 = true };
        chip.Clk1 = true;

        await Assert.That(chip.Q1).IsEqualTo(true);
        await Assert.That(chip.Qn1).IsEqualTo(false);

        chip.D1 = false;
        chip.Clk1 = false;
        await Assert.That(chip.Q1).IsEqualTo(true);

        chip.Clk1 = true;
        await Assert.That(chip.Q1).IsEqualTo(false);
        await Assert.That(chip.Qn1).IsEqualTo(true);
    }

    [Test]
    public async Task SetAndResetAreActiveHighAndAsynchronous()
    {
        var chip = new Cd4013Chip();

        chip.Set2 = true;
        await Assert.That(chip.Q2).IsEqualTo(true);
        await Assert.That(chip.Qn2).IsEqualTo(false);

        chip.Set2 = false;
        await Assert.That(chip.Q2).IsEqualTo(true);

        chip.Reset2 = true;
        await Assert.That(chip.Q2).IsEqualTo(false);
        await Assert.That(chip.Qn2).IsEqualTo(true);
    }

    [Test]
    public async Task SetAndResetTogetherForceBothOutputsHigh()
    {
        var chip = new Cd4013Chip { Set1 = true, Reset1 = true };

        await Assert.That(chip.Q1).IsEqualTo(true);
        await Assert.That(chip.Qn1).IsEqualTo(true);
    }

    [Test]
    public async Task ClockIgnoredWhileSetOrResetAsserted()
    {
        var chip = new Cd4013Chip { D1 = true, Reset1 = true };
        chip.Clk1 = true;
        await Assert.That(chip.Q1).IsEqualTo(false);

        var other = new Cd4013Chip { D1 = false, Set1 = true };
        other.Clk1 = true;
        await Assert.That(other.Q1).IsEqualTo(true);
    }

    [Test]
    public async Task FlipFlopsAreIndependent()
    {
        var chip = new Cd4013Chip { D1 = true };
        chip.Clk1 = true;

        await Assert.That(chip.Q1).IsEqualTo(true);
        await Assert.That(chip.Q2).IsEqualTo(false);
        await Assert.That(chip.Qn2).IsEqualTo(true);
    }
}
