using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl74244ChipTests
{
    [Test]
    public async Task PassesInputsWhenEnabled()
    {
        var chip = new Ttl74244Chip { A1 = true, A4 = true, A5 = true, A8 = true };

        await Assert.That(chip.Y1).IsEqualTo(true);
        await Assert.That(chip.Y2).IsEqualTo(false);
        await Assert.That(chip.Y4).IsEqualTo(true);
        await Assert.That(chip.Y5).IsEqualTo(true);
        await Assert.That(chip.Y8).IsEqualTo(true);
    }

    [Test]
    public async Task EachGroupHasItsOwnEnable()
    {
        var chip = new Ttl74244Chip { G1 = true, G2 = false, A1 = true, A5 = true };

        await Assert.That(chip.Y1).IsNull();
        await Assert.That(chip.Y4).IsNull();
        await Assert.That(chip.Y5).IsEqualTo(true);
        await Assert.That(chip.Y8).IsEqualTo(false);

        chip.G1 = false;
        chip.G2 = true;

        await Assert.That(chip.Y1).IsEqualTo(true);
        await Assert.That(chip.Y5).IsNull();
        await Assert.That(chip.Y8).IsNull();
    }
}
