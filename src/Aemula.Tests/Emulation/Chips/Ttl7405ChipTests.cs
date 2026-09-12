using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl7405ChipTests
{
    [Test]
    public async Task PullsOutputLowWhenInputHigh()
    {
        var chip = new Ttl7405Chip { A1 = true };

        await Assert.That(chip.Y1).IsEqualTo(false);
    }

    [Test]
    public async Task FloatsOutputWhenInputLow()
    {
        var chip = new Ttl7405Chip { A1 = false };

        await Assert.That(chip.Y1).IsNull();
    }

    [Test]
    public async Task GatesAreIndependent()
    {
        var chip = new Ttl7405Chip { A1 = true, A2 = false };

        await Assert.That(chip.Y1).IsEqualTo(false);
        await Assert.That(chip.Y2).IsNull();
    }
}
