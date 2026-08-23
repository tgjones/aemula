using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl74259ChipTests
{
    private static Ttl74259Chip CreateSelected(int index)
    {
        return new Ttl74259Chip
        {
            A0 = (index & 1) != 0,
            A1 = (index & 2) != 0,
            A2 = (index & 4) != 0,
            G = false,
        };
    }

    [Test]
    public async Task SelectedOutputIsTransparentToData()
    {
        var chip = CreateSelected(3);

        chip.D = true;
        await Assert.That(chip.Q3).IsEqualTo(true);

        chip.D = false;
        await Assert.That(chip.Q3).IsEqualTo(false);
    }

    [Test]
    public async Task MovingAddressFreezesPreviouslySelectedOutput()
    {
        var chip = CreateSelected(3);
        chip.D = true;

        // Move the address from 3 (011) to 5 (101).
        chip.A0 = true;
        chip.A1 = false;
        chip.A2 = true;

        // Q3 is no longer selected, so it should hold its last value (true)
        // even though D changes; Q5 is now selected, so it should track D.
        chip.D = false;

        await Assert.That(chip.Q3).IsEqualTo(true);
        await Assert.That(chip.Q5).IsEqualTo(false);
    }

    [Test]
    public async Task AllOutputsHoldWhenDisabled()
    {
        var chip = new Ttl74259Chip { A0 = true, G = true };

        chip.D = true;

        await Assert.That(chip.Q1).IsEqualTo(false);
    }

    [Test]
    public async Task AsyncClearForcesAllOutputsLowRegardlessOfEnable()
    {
        var chip = CreateSelected(3);
        chip.D = true;

        chip.Clr = false;

        await Assert.That(chip.Q3).IsEqualTo(false);
    }

    [Test]
    public async Task ReleasingClearReestablishesTransparencyImmediately()
    {
        var chip = CreateSelected(3);
        chip.D = true;
        chip.Clr = false;

        chip.Clr = true;

        await Assert.That(chip.Q3).IsEqualTo(true);
    }
}
