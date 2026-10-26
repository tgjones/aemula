using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl74132ChipTests
{
    [Test]
    public async Task NandTruthTable()
    {
        foreach (var (a, b, y) in new[] { (false, false, true), (false, true, true), (true, false, true), (true, true, false) })
        {
            var chip = new Ttl74132Chip { A1 = a, B1 = b, A2 = a, B2 = b, A3 = a, B3 = b, A4 = a, B4 = b };
            await Assert.That(chip.Y1).IsEqualTo(y);
            await Assert.That(chip.Y2).IsEqualTo(y);
            await Assert.That(chip.Y3).IsEqualTo(y);
            await Assert.That(chip.Y4).IsEqualTo(y);
        }
    }

    [Test]
    public async Task GatesAreIndependent()
    {
        var chip = new Ttl74132Chip { A3 = true, B3 = true };
        await Assert.That(chip.Y3).IsEqualTo(false);
        await Assert.That(chip.Y1).IsEqualTo(true);
    }
}
