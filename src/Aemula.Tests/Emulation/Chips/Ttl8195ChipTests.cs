using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl8195ChipTests
{
    [Test]
    public async Task DrivesWhenBothEnablesLow()
    {
        var chip = new Ttl8195Chip { A1 = true, A8 = true };

        await Assert.That(chip.Y1).IsEqualTo(true);
        await Assert.That(chip.Y2).IsEqualTo(false);
        await Assert.That(chip.Y8).IsEqualTo(true);
    }

    [Test]
    public async Task FloatsWhenEitherEnableHigh()
    {
        var chip = new Ttl8195Chip { G1 = true, A1 = true };
        await Assert.That(chip.Y1).IsNull();
        await Assert.That(chip.Y8).IsNull();

        chip = new Ttl8195Chip { G2 = true, A1 = true };
        await Assert.That(chip.Y1).IsNull();
        await Assert.That(chip.Y8).IsNull();
    }
}
