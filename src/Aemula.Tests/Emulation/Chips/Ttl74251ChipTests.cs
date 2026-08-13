using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl74251ChipTests
{
    [Test]
    [Arguments(false, false, false, 0)]
    [Arguments(true, false, false, 1)]
    [Arguments(false, true, false, 2)]
    [Arguments(true, true, false, 3)]
    [Arguments(false, false, true, 4)]
    [Arguments(true, false, true, 5)]
    [Arguments(false, true, true, 6)]
    [Arguments(true, true, true, 7)]
    public async Task SelectsCorrectInputWhenEnabled(bool a, bool b, bool c, int selected)
    {
        var chip = new Ttl74251Chip { A = a, B = b, C = c, S = false };

        var data = new bool[8];
        data[selected] = true;

        chip.D0 = data[0];
        chip.D1 = data[1];
        chip.D2 = data[2];
        chip.D3 = data[3];
        chip.D4 = data[4];
        chip.D5 = data[5];
        chip.D6 = data[6];
        chip.D7 = data[7];

        await Assert.That(chip.Y).IsEqualTo((bool?)true);
        await Assert.That(chip.W).IsEqualTo((bool?)false);
    }

    [Test]
    public async Task OutputsAreHighImpedanceWhenStrobeIsHigh()
    {
        var chip = new Ttl74251Chip { A = false, B = false, C = false, S = true, D0 = true };

        await Assert.That(chip.Y).IsEqualTo((bool?)null);
        await Assert.That(chip.W).IsEqualTo((bool?)null);
    }
}
