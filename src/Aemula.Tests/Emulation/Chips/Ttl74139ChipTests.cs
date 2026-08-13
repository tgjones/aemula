using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl74139ChipTests
{
    [Test]
    [Arguments(false, false, 0)]
    [Arguments(true, false, 1)]
    [Arguments(false, true, 2)]
    [Arguments(true, true, 3)]
    public async Task DrivesOnlySelectedOutputLowWhenEnabled(bool a, bool b, int selected)
    {
        var chip = new Ttl74139Chip { A1 = a, B1 = b, G1 = false };

        var outputs = new[] { chip.Y1_0, chip.Y1_1, chip.Y1_2, chip.Y1_3 };

        for (var i = 0; i < outputs.Length; i++)
        {
            await Assert.That(outputs[i]).IsEqualTo(i != selected);
        }
    }

    [Test]
    public async Task AllOutputsInactiveWhenGIsHigh()
    {
        var chip = new Ttl74139Chip { A1 = false, B1 = false, G1 = true };

        await Assert.That(chip.Y1_0).IsEqualTo(true);
    }

    [Test]
    public async Task UnitsAreIndependent()
    {
        var chip = new Ttl74139Chip
        {
            A1 = false,
            B1 = false,
            G1 = false,
            A2 = true,
            B2 = true,
            G2 = false,
        };

        await Assert.That(chip.Y1_0).IsEqualTo(false);
        await Assert.That(chip.Y2_3).IsEqualTo(false);
        await Assert.That(chip.Y2_0).IsEqualTo(true);
    }
}
