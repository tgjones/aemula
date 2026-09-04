using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl7450ChipTests
{
    [Test]
    [Arguments(false, false, false, false, true)]
    [Arguments(true, true, false, false, false)]
    [Arguments(false, false, true, true, false)]
    [Arguments(true, true, true, true, false)]
    [Arguments(true, false, true, false, true)]
    public async Task TruthTable(bool a, bool b, bool c, bool d, bool expectedY)
    {
        var chip = new Ttl7450Chip { A1 = a, B1 = b, C1 = c, D1 = d };

        await Assert.That(chip.Y1).IsEqualTo(expectedY);
    }

    [Test]
    public async Task GatesAreIndependent()
    {
        var chip = new Ttl7450Chip
        {
            A1 = true,
            B1 = true,
            C1 = false,
            D1 = false,
            A2 = false,
            B2 = false,
            C2 = false,
            D2 = false,
        };

        await Assert.That(chip.Y1).IsEqualTo(false);
        await Assert.That(chip.Y2).IsEqualTo(true);
    }
}
