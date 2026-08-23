using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl7402ChipTests
{
    [Test]
    [Arguments(false, false, true)]
    [Arguments(false, true, false)]
    [Arguments(true, false, false)]
    [Arguments(true, true, false)]
    public async Task TruthTable(bool a, bool b, bool expectedY)
    {
        var chip = new Ttl7402Chip { A1 = a, B1 = b };

        await Assert.That(chip.Y1).IsEqualTo(expectedY);
    }

    [Test]
    public async Task GatesAreIndependent()
    {
        var chip = new Ttl7402Chip
        {
            A1 = false,
            B1 = false,
            A2 = true,
            B2 = true,
        };

        await Assert.That(chip.Y1).IsEqualTo(true);
        await Assert.That(chip.Y2).IsEqualTo(false);
    }
}
