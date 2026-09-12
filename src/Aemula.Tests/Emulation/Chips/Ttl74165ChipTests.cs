using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl74165ChipTests
{
    [Test]
    public async Task AsyncLoadIsTransparentWhileShLdLow()
    {
        var chip = new Ttl74165Chip { ShLd = false, H = true };

        await Assert.That(chip.Qh).IsEqualTo(true);

        chip.H = false;

        await Assert.That(chip.Qh).IsEqualTo(false);
    }

    [Test]
    public async Task ShiftsSerIntoAOnRisingEdgeInShiftMode()
    {
        var chip = new Ttl74165Chip { ShLd = false, A = false, B = false, C = false, D = false, E = false, F = false, G = false, H = false };
        chip.ShLd = true;
        chip.Ser = true;

        chip.Clk = true;

        // Ser -> A -> ... -> H takes 8 clocks to reach Qh.
        for (var i = 0; i < 7; i++)
        {
            chip.Clk = false;
            chip.Clk = true;
        }

        await Assert.That(chip.Qh).IsEqualTo(true);
    }

    [Test]
    public async Task ClockInhibitBlocksShifting()
    {
        var chip = new Ttl74165Chip { ShLd = true, ClkInh = true, Ser = true };

        for (var i = 0; i < 8; i++)
        {
            chip.Clk = false;
            chip.Clk = true;
        }

        await Assert.That(chip.Qh).IsEqualTo(false);
    }

    [Test]
    public async Task QhNIsComplementOfQh()
    {
        var chip = new Ttl74165Chip { ShLd = false, H = true };

        await Assert.That(chip.QhN).IsEqualTo(false);
    }

    [Test]
    public async Task LoadOverridesClockWhileShLdLow()
    {
        var chip = new Ttl74165Chip { ShLd = false, H = false, Ser = true };

        chip.Clk = false;
        chip.Clk = true;

        await Assert.That(chip.Qh).IsEqualTo(false);
    }
}
