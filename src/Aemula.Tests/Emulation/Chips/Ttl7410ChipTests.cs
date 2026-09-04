using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl7410ChipTests
{
    [Test]
    [Arguments(false, false, false, true)]
    [Arguments(false, false, true, true)]
    [Arguments(false, true, false, true)]
    [Arguments(false, true, true, true)]
    [Arguments(true, false, false, true)]
    [Arguments(true, false, true, true)]
    [Arguments(true, true, false, true)]
    [Arguments(true, true, true, false)]
    public async Task TruthTable(bool a, bool b, bool c, bool expectedY)
    {
        var chip = new Ttl7410Chip { A1 = a, B1 = b, C1 = c };

        await Assert.That(chip.Y1).IsEqualTo(expectedY);
    }

    [Test]
    public async Task GatesAreIndependent()
    {
        var chip = new Ttl7410Chip
        {
            A1 = true,
            B1 = true,
            C1 = true,
            A2 = true,
            B2 = true,
            C2 = false,
        };

        await Assert.That(chip.Y1).IsEqualTo(false);
        await Assert.That(chip.Y2).IsEqualTo(true);
    }
}
