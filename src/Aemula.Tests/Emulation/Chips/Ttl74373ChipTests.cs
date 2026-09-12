using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl74373ChipTests
{
    [Test]
    public async Task IsTransparentWhileLatchEnableHigh()
    {
        var chip = new Ttl74373Chip { Le = true, D0 = true };

        await Assert.That(chip.Q0).IsEqualTo(true);

        chip.D0 = false;

        await Assert.That(chip.Q0).IsEqualTo(false);
    }

    [Test]
    public async Task FreezesOutputOnLatchEnableFallingEdge()
    {
        var chip = new Ttl74373Chip { Le = true, D0 = true };

        chip.Le = false;
        chip.D0 = false;

        await Assert.That(chip.Q0).IsEqualTo(true);
    }

    [Test]
    public async Task ResyncsToLiveDataOnLatchEnableRisingEdge()
    {
        var chip = new Ttl74373Chip { Le = true, D0 = true };
        chip.Le = false;
        chip.D0 = false; // Changes while latched - shouldn't take effect yet.

        chip.Le = true;

        await Assert.That(chip.Q0).IsEqualTo(false);
    }

    [Test]
    public async Task OutputEnableHighFloatsOutputsRegardlessOfLatchState()
    {
        var chip = new Ttl74373Chip { Le = true, D0 = true, Oe = true };

        await Assert.That(chip.Q0).IsNull();
    }

    [Test]
    public async Task ChannelsAreIndependent()
    {
        var chip = new Ttl74373Chip { Le = true, D0 = true, D1 = false };

        await Assert.That(chip.Q0).IsEqualTo(true);
        await Assert.That(chip.Q1).IsEqualTo(false);
    }
}
