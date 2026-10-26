using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl74245ChipTests
{
    [Test]
    public async Task DirHighPassesAToB()
    {
        var chip = new Ttl74245Chip { Dir = true, A0 = true, A7 = false, B3 = true };

        await Assert.That(chip.B0Out).IsEqualTo(true);
        await Assert.That(chip.B7Out).IsEqualTo(false);
        await Assert.That(chip.A3Out).IsNull();
        await Assert.That(chip.A0Out).IsNull();
    }

    [Test]
    public async Task DirLowPassesBToA()
    {
        var chip = new Ttl74245Chip { Dir = false, B2 = true, B5 = false, A1 = true };

        await Assert.That(chip.A2Out).IsEqualTo(true);
        await Assert.That(chip.A5Out).IsEqualTo(false);
        await Assert.That(chip.B1Out).IsNull();
    }

    [Test]
    public async Task BothSidesFloatWhenDisabled()
    {
        var chip = new Ttl74245Chip { G = true, Dir = true, A0 = true, B0 = true };

        await Assert.That(chip.A0Out).IsNull();
        await Assert.That(chip.B0Out).IsNull();

        chip.Dir = false;
        await Assert.That(chip.A0Out).IsNull();
        await Assert.That(chip.B0Out).IsNull();
    }

    [Test]
    public async Task UndrivenSourceGivesFloatingOutput()
    {
        var chip = new Ttl74245Chip { Dir = true };
        await Assert.That(chip.B4Out).IsNull();
    }
}
