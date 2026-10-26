using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl7430ChipTests
{
    [Test]
    public async Task OutputLowOnlyWhenAllEightInputsHigh()
    {
        var chip = new Ttl7430Chip { A = true, B = true, C = true, D = true, E = true, F = true, G = true, H = true };
        await Assert.That(chip.Y).IsEqualTo(false);
    }

    [Test]
    public async Task OutputHighWhenAnySingleInputLow()
    {
        for (var low = 0; low < 8; low++)
        {
            var chip = new Ttl7430Chip
            {
                A = low != 0, B = low != 1, C = low != 2, D = low != 3,
                E = low != 4, F = low != 5, G = low != 6, H = low != 7,
            };
            await Assert.That(chip.Y).IsEqualTo(true);
        }
    }
}
