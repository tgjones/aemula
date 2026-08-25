using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl7455ChipTests
{
    [Test]
    public async Task YIsHighWhenBothAndTermsAreLow()
    {
        var chip = new Ttl7455Chip();

        await Assert.That(chip.Y).IsEqualTo(true);
    }

    [Test]
    [Arguments(false, true, true, true)]
    [Arguments(true, false, true, true)]
    [Arguments(true, true, false, true)]
    [Arguments(true, true, true, false)]
    public async Task FirstAndTermRequiresAllFourInputs(bool a1, bool b1, bool c1, bool d1)
    {
        var chip = new Ttl7455Chip { A1 = a1, B1 = b1, C1 = c1, D1 = d1 };

        await Assert.That(chip.Y).IsEqualTo(true);
    }

    [Test]
    public async Task YIsLowWhenFirstAndTermIsFullyHigh()
    {
        var chip = new Ttl7455Chip { A1 = true, B1 = true, C1 = true, D1 = true };

        await Assert.That(chip.Y).IsEqualTo(false);
    }

    [Test]
    public async Task YIsLowWhenSecondAndTermIsFullyHigh()
    {
        var chip = new Ttl7455Chip { A2 = true, B2 = true, C2 = true, D2 = true };

        await Assert.That(chip.Y).IsEqualTo(false);
    }

    [Test]
    public async Task YIsLowWhenBothAndTermsAreFullyHigh()
    {
        var chip = new Ttl7455Chip
        {
            A1 = true,
            B1 = true,
            C1 = true,
            D1 = true,
            A2 = true,
            B2 = true,
            C2 = true,
            D2 = true,
        };

        await Assert.That(chip.Y).IsEqualTo(false);
    }
}
