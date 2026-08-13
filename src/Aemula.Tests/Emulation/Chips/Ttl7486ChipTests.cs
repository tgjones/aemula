using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl7486ChipTests
{
    [Test]
    [Arguments(false, false, false)]
    [Arguments(false, true, true)]
    [Arguments(true, false, true)]
    [Arguments(true, true, false)]
    public async Task TruthTable(bool a, bool b, bool expectedY)
    {
        var chip = new Ttl7486Chip { A1 = a, B1 = b };

        await Assert.That(chip.Y1).IsEqualTo(expectedY);
    }

    [Test]
    public async Task GatesAreIndependent()
    {
        var chip = new Ttl7486Chip
        {
            A1 = true,
            B1 = true,
            A2 = true,
            B2 = false,
        };

        await Assert.That(chip.Y1).IsEqualTo(false);
        await Assert.That(chip.Y2).IsEqualTo(true);
    }
}
