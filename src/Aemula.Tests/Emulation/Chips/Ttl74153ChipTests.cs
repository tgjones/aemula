using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl74153ChipTests
{
    [Test]
    [Arguments(false, false, 0)]
    [Arguments(true, false, 1)]
    [Arguments(false, true, 2)]
    [Arguments(true, true, 3)]
    public async Task SelectsCorrectInputWhenEnabled(bool a, bool b, int selected)
    {
        var chip = new Ttl74153Chip { A = a, B = b, G1 = false };

        var data = new bool[4];
        data[selected] = true;

        chip.C1_0 = data[0];
        chip.C1_1 = data[1];
        chip.C1_2 = data[2];
        chip.C1_3 = data[3];

        await Assert.That(chip.Y1).IsEqualTo(true);
    }

    [Test]
    public async Task OutputForcedLowWhenStrobeIsHigh()
    {
        var chip = new Ttl74153Chip { A = false, B = false, G1 = true, C1_0 = true };

        await Assert.That(chip.Y1).IsEqualTo(false);
    }

    [Test]
    public async Task UnitsShareSelectLinesButHaveIndependentDataAndStrobe()
    {
        var chip = new Ttl74153Chip
        {
            A = true,
            B = false,
            G1 = false,
            G2 = true,
            C1_1 = true,
            C2_1 = true,
        };

        await Assert.That(chip.Y1).IsEqualTo(true);
        await Assert.That(chip.Y2).IsEqualTo(false);
    }
}
