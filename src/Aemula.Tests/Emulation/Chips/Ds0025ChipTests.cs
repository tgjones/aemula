using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ds0025ChipTests
{
    [Test]
    [Arguments(false, true)]
    [Arguments(true, false)]
    public async Task ChannelOneInverts(bool input, bool expectedOutput)
    {
        var chip = new Ds0025Chip { In1 = input };

        await Assert.That(chip.Out1).IsEqualTo(expectedOutput);
    }

    [Test]
    public async Task ChannelsAreIndependent()
    {
        var chip = new Ds0025Chip { In1 = false, In2 = true };

        await Assert.That(chip.Out1).IsEqualTo(true);
        await Assert.That(chip.Out2).IsEqualTo(false);
    }
}
