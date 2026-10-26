using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl7438ChipTests
{
    [Test]
    public async Task PullsLowWhenBothInputsHigh()
    {
        var chip = new Ttl7438Chip { A1 = true, B1 = true, A4 = true, B4 = true };
        await Assert.That(chip.Y1).IsEqualTo(false);
        await Assert.That(chip.Y4).IsEqualTo(false);
    }

    [Test]
    public async Task FloatsInsteadOfDrivingHigh()
    {
        var chip = new Ttl7438Chip { A2 = true };
        await Assert.That(chip.Y1).IsNull();
        await Assert.That(chip.Y2).IsNull();
        await Assert.That(chip.Y3).IsNull();
    }

    [Test]
    public async Task GatesAreIndependent()
    {
        var chip = new Ttl7438Chip { A3 = true, B3 = true };
        await Assert.That(chip.Y3).IsEqualTo(false);
        await Assert.That(chip.Y1).IsNull();
        await Assert.That(chip.Y2).IsNull();
        await Assert.That(chip.Y4).IsNull();
    }
}
