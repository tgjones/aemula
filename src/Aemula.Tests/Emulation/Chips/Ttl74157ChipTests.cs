using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl74157ChipTests
{
    [Test]
    public async Task SelectsAInputWhenSIsLow()
    {
        var chip = new Ttl74157Chip { S = false, G = false, A1 = true, B1 = false };

        await Assert.That(chip.Y1).IsEqualTo(true);
    }

    [Test]
    public async Task SelectsBInputWhenSIsHigh()
    {
        var chip = new Ttl74157Chip { S = true, G = false, A1 = false, B1 = true };

        await Assert.That(chip.Y1).IsEqualTo(true);
    }

    [Test]
    public async Task OutputIsForcedLowWhenStrobeIsHigh()
    {
        var chip = new Ttl74157Chip { S = false, G = true, A1 = true, B1 = false };

        await Assert.That(chip.Y1).IsEqualTo(false);
    }

    [Test]
    public async Task ChannelsAreIndependent()
    {
        var chip = new Ttl74157Chip
        {
            S = false,
            G = false,
            A1 = true,
            B1 = false,
            A2 = false,
            B2 = true,
        };

        await Assert.That(chip.Y1).IsEqualTo(true);
        await Assert.That(chip.Y2).IsEqualTo(false);
    }

    [Test]
    public async Task SelectAndStrobeAreSharedAcrossAllChannels()
    {
        var chip = new Ttl74157Chip
        {
            S = true,
            G = false,
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
