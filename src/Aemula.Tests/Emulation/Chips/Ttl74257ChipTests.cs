using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl74257ChipTests
{
    [Test]
    public async Task SelectsAInputWhenSIsLow()
    {
        var chip = new Ttl74257Chip { S = false, Oe = false, A1 = true, B1 = false };

        await Assert.That(chip.Y1).IsEqualTo(true);
    }

    [Test]
    public async Task SelectsBInputWhenSIsHigh()
    {
        var chip = new Ttl74257Chip { S = true, Oe = false, A1 = false, B1 = true };

        await Assert.That(chip.Y1).IsEqualTo(true);
    }

    [Test]
    public async Task OutputIsHighImpedanceWhenOutputEnableIsHigh()
    {
        var chip = new Ttl74257Chip { S = false, Oe = true, A1 = true, B1 = false };

        await Assert.That(chip.Y1).IsEqualTo((bool?)null);
    }

    [Test]
    public async Task ChannelsAreIndependent()
    {
        var chip = new Ttl74257Chip
        {
            S = false,
            Oe = false,
            A1 = true,
            B1 = false,
            A2 = false,
            B2 = true,
        };

        await Assert.That(chip.Y1).IsEqualTo(true);
        await Assert.That(chip.Y2).IsEqualTo(false);
    }

    [Test]
    public async Task SelectLineIsSharedAcrossAllChannels()
    {
        var chip = new Ttl74257Chip
        {
            S = true,
            Oe = false,
            B1 = true,
            B2 = false,
            B3 = true,
            B4 = false,
        };

        await Assert.That(chip.Y1).IsEqualTo(true);
        await Assert.That(chip.Y2).IsEqualTo(false);
        await Assert.That(chip.Y3).IsEqualTo(true);
        await Assert.That(chip.Y4).IsEqualTo(false);
    }
}
