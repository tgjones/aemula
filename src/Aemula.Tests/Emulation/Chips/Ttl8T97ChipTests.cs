using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl8T97ChipTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(true, true)]
    public async Task PassesInputThroughWhenEnabled(bool a, bool expectedY)
    {
        var chip = new Ttl8T97Chip { Oe = false, A1 = a };

        await Assert.That(chip.Y1).IsEqualTo((bool?)expectedY);
    }

    [Test]
    public async Task OutputIsHighImpedanceWhenDisabled()
    {
        var chip = new Ttl8T97Chip { Oe = true, A1 = true };

        await Assert.That(chip.Y1).IsEqualTo((bool?)null);
    }

    [Test]
    public async Task BuffersAreIndependent()
    {
        var chip = new Ttl8T97Chip
        {
            Oe = false,
            A1 = true,
            A2 = false,
        };

        await Assert.That(chip.Y1).IsEqualTo((bool?)true);
        await Assert.That(chip.Y2).IsEqualTo((bool?)false);
    }
}
