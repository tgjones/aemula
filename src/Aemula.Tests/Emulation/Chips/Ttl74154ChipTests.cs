using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl74154ChipTests
{
    private static Ttl74154Chip CreateEnabled(bool a, bool b, bool c, bool d)
    {
        return new Ttl74154Chip
        {
            A = a,
            B = b,
            C = c,
            D = d,
            G1 = false,
            G2 = false,
        };
    }

    [Test]
    [Arguments(false, false, false, false, 0)]
    [Arguments(true, false, false, false, 1)]
    [Arguments(false, true, false, false, 2)]
    [Arguments(true, true, false, false, 3)]
    [Arguments(false, false, true, false, 4)]
    [Arguments(false, false, false, true, 8)]
    [Arguments(true, false, false, true, 9)]
    [Arguments(true, true, true, true, 15)]
    public async Task DrivesOnlySelectedOutputLowWhenEnabled(bool a, bool b, bool c, bool d, int selected)
    {
        var chip = CreateEnabled(a, b, c, d);

        var outputs = new[]
        {
            chip.Y0, chip.Y1, chip.Y2, chip.Y3,
            chip.Y4, chip.Y5, chip.Y6, chip.Y7,
            chip.Y8, chip.Y9, chip.Y10, chip.Y11,
            chip.Y12, chip.Y13, chip.Y14, chip.Y15,
        };

        for (var i = 0; i < outputs.Length; i++)
        {
            await Assert.That(outputs[i]).IsEqualTo(i != selected);
        }
    }

    [Test]
    public async Task AllOutputsInactiveWhenG1IsHigh()
    {
        var chip = CreateEnabled(false, false, false, false);
        chip.G1 = true;

        await Assert.That(chip.Y0).IsEqualTo(true);
    }

    [Test]
    public async Task AllOutputsInactiveWhenG2IsHigh()
    {
        var chip = CreateEnabled(false, false, false, false);
        chip.G2 = true;

        await Assert.That(chip.Y0).IsEqualTo(true);
    }
}
