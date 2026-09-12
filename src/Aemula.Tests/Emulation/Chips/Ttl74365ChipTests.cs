using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl74365ChipTests
{
    [Test]
    public async Task DrivesOutputWhenGroup1Enabled()
    {
        var chip = new Ttl74365Chip { G1 = false, A1 = true };

        await Assert.That(chip.Y1).IsEqualTo(true);
    }

    [Test]
    public async Task FloatsGroup1OutputsWhenGroup1Disabled()
    {
        var chip = new Ttl74365Chip { G1 = true, A1 = true };

        await Assert.That(chip.Y1).IsNull();
    }

    [Test]
    public async Task Group1AndGroup2EnablesAreIndependent()
    {
        var chip = new Ttl74365Chip { G1 = true, G2 = false, A1 = true, A5 = true };

        await Assert.That(chip.Y1).IsNull();
        await Assert.That(chip.Y5).IsEqualTo(true);
    }
}
