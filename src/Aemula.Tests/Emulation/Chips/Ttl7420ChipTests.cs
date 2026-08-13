using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl7420ChipTests
{
    [Test]
    [Arguments(false, false, false, false, true)]
    [Arguments(false, false, false, true, true)]
    [Arguments(false, false, true, false, true)]
    [Arguments(false, false, true, true, true)]
    [Arguments(false, true, false, false, true)]
    [Arguments(false, true, false, true, true)]
    [Arguments(false, true, true, false, true)]
    [Arguments(false, true, true, true, true)]
    [Arguments(true, false, false, false, true)]
    [Arguments(true, false, false, true, true)]
    [Arguments(true, false, true, false, true)]
    [Arguments(true, false, true, true, true)]
    [Arguments(true, true, false, false, true)]
    [Arguments(true, true, false, true, true)]
    [Arguments(true, true, true, false, true)]
    [Arguments(true, true, true, true, false)]
    public async Task TruthTable(bool a, bool b, bool c, bool d, bool expectedY)
    {
        var chip = new Ttl7420Chip { A1 = a, B1 = b, C1 = c, D1 = d };

        await Assert.That(chip.Y1).IsEqualTo(expectedY);
    }

    [Test]
    public async Task GatesAreIndependent()
    {
        var chip = new Ttl7420Chip
        {
            A1 = true,
            B1 = true,
            C1 = true,
            D1 = true,
            A2 = true,
            B2 = true,
            C2 = true,
            D2 = false,
        };

        await Assert.That(chip.Y1).IsEqualTo(false);
        await Assert.That(chip.Y2).IsEqualTo(true);
    }
}
