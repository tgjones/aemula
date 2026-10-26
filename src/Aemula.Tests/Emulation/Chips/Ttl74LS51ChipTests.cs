using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl74LS51ChipTests
{
    [Test]
    public async Task Gate1LowWhenFirstThreeInputTermTrue()
    {
        var chip = new Ttl74LS51Chip { A1 = true, B1 = true, C1 = true };
        await Assert.That(chip.Y1).IsEqualTo(false);
    }

    [Test]
    public async Task Gate1LowWhenSecondThreeInputTermTrue()
    {
        var chip = new Ttl74LS51Chip { D1 = true, E1 = true, F1 = true };
        await Assert.That(chip.Y1).IsEqualTo(false);
    }

    [Test]
    public async Task Gate1HighWhenNoTermComplete()
    {
        var chip = new Ttl74LS51Chip { A1 = true, B1 = true, D1 = true, E1 = true };
        await Assert.That(chip.Y1).IsEqualTo(true);
    }

    [Test]
    public async Task Gate2IsTwoWideTwoInput()
    {
        var chip = new Ttl74LS51Chip { A2 = true, B2 = true };
        await Assert.That(chip.Y2).IsEqualTo(false);

        chip = new Ttl74LS51Chip { C2 = true, D2 = true };
        await Assert.That(chip.Y2).IsEqualTo(false);

        chip = new Ttl74LS51Chip { A2 = true, C2 = true };
        await Assert.That(chip.Y2).IsEqualTo(true);
    }

    [Test]
    public async Task AllInputsLowGivesHighOutputs()
    {
        var chip = new Ttl74LS51Chip();
        await Assert.That(chip.Y1).IsEqualTo(true);
        await Assert.That(chip.Y2).IsEqualTo(true);
    }
}
