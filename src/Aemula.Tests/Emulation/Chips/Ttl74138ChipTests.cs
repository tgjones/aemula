using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl74138ChipTests
{
    private static Ttl74138Chip CreateEnabled(bool a, bool b, bool c)
    {
        return new Ttl74138Chip
        {
            A = a,
            B = b,
            C = c,
            G1 = true,
            G2A = false,
            G2B = false,
        };
    }

    [Test]
    [Arguments(false, false, false, 0)]
    [Arguments(true, false, false, 1)]
    [Arguments(false, true, false, 2)]
    [Arguments(true, true, false, 3)]
    [Arguments(false, false, true, 4)]
    [Arguments(true, false, true, 5)]
    [Arguments(false, true, true, 6)]
    [Arguments(true, true, true, 7)]
    public async Task DrivesOnlySelectedOutputLowWhenEnabled(bool a, bool b, bool c, int selected)
    {
        var chip = CreateEnabled(a, b, c);

        var outputs = new[] { chip.Y0, chip.Y1, chip.Y2, chip.Y3, chip.Y4, chip.Y5, chip.Y6, chip.Y7 };

        for (var i = 0; i < outputs.Length; i++)
        {
            await Assert.That(outputs[i]).IsEqualTo(i != selected);
        }
    }

    [Test]
    public async Task AllOutputsInactiveWhenG1IsLow()
    {
        var chip = CreateEnabled(false, false, false);
        chip.G1 = false;

        await Assert.That(chip.Y0).IsEqualTo(true);
    }

    [Test]
    public async Task AllOutputsInactiveWhenG2AIsHigh()
    {
        var chip = CreateEnabled(false, false, false);
        chip.G2A = true;

        await Assert.That(chip.Y0).IsEqualTo(true);
    }

    [Test]
    public async Task AllOutputsInactiveWhenG2BIsHigh()
    {
        var chip = CreateEnabled(false, false, false);
        chip.G2B = true;

        await Assert.That(chip.Y0).IsEqualTo(true);
    }
}
