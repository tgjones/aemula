using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl7404ChipTests
{
    [Test]
    [Arguments(false, true)]
    [Arguments(true, false)]
    public async Task TruthTable(bool a, bool expectedY)
    {
        var chip = new Ttl7404Chip { A1 = a };

        await Assert.That(chip.Y1).IsEqualTo(expectedY);
    }

    [Test]
    public async Task GatesAreIndependent()
    {
        var chip = new Ttl7404Chip
        {
            A1 = false,
            A2 = true,
        };

        await Assert.That(chip.Y1).IsEqualTo(true);
        await Assert.That(chip.Y2).IsEqualTo(false);
    }
}
