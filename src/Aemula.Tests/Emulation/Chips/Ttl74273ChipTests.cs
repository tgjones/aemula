using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl74273ChipTests
{
    [Test]
    public async Task CapturesInputsOnRisingEdgeOnly()
    {
        var chip = new Ttl74273Chip { D0 = true, D7 = true };
        await Assert.That(chip.Q0).IsEqualTo(false);

        chip.Clk = true;
        await Assert.That(chip.Q0).IsEqualTo(true);
        await Assert.That(chip.Q1).IsEqualTo(false);
        await Assert.That(chip.Q7).IsEqualTo(true);

        chip.D0 = false;
        await Assert.That(chip.Q0).IsEqualTo(true);

        chip.Clk = false;
        await Assert.That(chip.Q0).IsEqualTo(true);
    }

    [Test]
    public async Task ClearForcesAllOutputsLowImmediately()
    {
        var chip = new Ttl74273Chip { D3 = true, D4 = true };
        chip.Clk = true;
        await Assert.That(chip.Q3).IsEqualTo(true);

        chip.Clr = false;
        await Assert.That(chip.Q3).IsEqualTo(false);
        await Assert.That(chip.Q4).IsEqualTo(false);
    }

    [Test]
    public async Task ClockIgnoredWhileCleared()
    {
        var chip = new Ttl74273Chip { Clr = false, D0 = true };
        chip.Clk = true;
        await Assert.That(chip.Q0).IsEqualTo(false);
    }
}
