using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Signetics2519ChipTests
{
    private static void Pulse(Signetics2519Chip chip)
    {
        chip.Clk = false;
        chip.Clk = true;
    }

    [Test]
    public async Task ABitTakes40ClocksToReachOut()
    {
        var chip = new Signetics2519Chip { In3 = true };
        Pulse(chip);
        chip.In3 = false;

        for (var i = 0; i < 39; i++)
        {
            await Assert.That(chip.Out3).IsFalse();
            Pulse(chip);
        }

        await Assert.That(chip.Out3).IsTrue();
    }

    [Test]
    public async Task TheSixLanesAreIndependent()
    {
        var chip = new Signetics2519Chip { In1 = true, In6 = true };
        Pulse(chip);
        chip.In1 = false;
        chip.In6 = false;

        for (var i = 0; i < 39; i++)
        {
            Pulse(chip);
        }

        await Assert.That(chip.Out1).IsTrue();
        await Assert.That(chip.Out2).IsFalse();
        await Assert.That(chip.Out3).IsFalse();
        await Assert.That(chip.Out4).IsFalse();
        await Assert.That(chip.Out5).IsFalse();
        await Assert.That(chip.Out6).IsTrue();
    }
}
