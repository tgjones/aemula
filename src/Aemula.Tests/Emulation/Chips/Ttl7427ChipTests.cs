using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl7427ChipTests
{
    [Test]
    [Arguments(false, false, false, true)]
    [Arguments(false, false, true, false)]
    [Arguments(false, true, false, false)]
    [Arguments(false, true, true, false)]
    [Arguments(true, false, false, false)]
    [Arguments(true, false, true, false)]
    [Arguments(true, true, false, false)]
    [Arguments(true, true, true, false)]
    public async Task TruthTable(bool a, bool b, bool c, bool expectedY)
    {
        var chip = new Ttl7427Chip { A1 = a, B1 = b, C1 = c };

        await Assert.That(chip.Y1).IsEqualTo(expectedY);
    }

    [Test]
    public async Task GatesAreIndependent()
    {
        var chip = new Ttl7427Chip
        {
            A1 = false,
            B1 = false,
            C1 = false,
            A2 = true,
            B2 = false,
            C2 = false,
        };

        await Assert.That(chip.Y1).IsEqualTo(true);
        await Assert.That(chip.Y2).IsEqualTo(false);
    }
}
