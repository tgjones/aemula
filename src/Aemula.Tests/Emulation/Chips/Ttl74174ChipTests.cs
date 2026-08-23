using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl74174ChipTests
{
    [Test]
    public async Task LatchesAllBitsOnRisingEdge()
    {
        var chip = new Ttl74174Chip
        {
            D1 = true,
            D2 = false,
            D3 = true,
            D4 = false,
            D5 = true,
            D6 = false,
        };

        chip.Clk = true;

        await Assert.That(chip.Q1).IsEqualTo(true);
        await Assert.That(chip.Q2).IsEqualTo(false);
        await Assert.That(chip.Q3).IsEqualTo(true);
        await Assert.That(chip.Q4).IsEqualTo(false);
        await Assert.That(chip.Q5).IsEqualTo(true);
        await Assert.That(chip.Q6).IsEqualTo(false);
    }

    [Test]
    public async Task IgnoresDWhileClockIsHigh()
    {
        var chip = new Ttl74174Chip { D1 = true };
        chip.Clk = true;

        chip.D1 = false;

        await Assert.That(chip.Q1).IsEqualTo(true);
    }

    [Test]
    public async Task IgnoresDOnFallingEdge()
    {
        var chip = new Ttl74174Chip { D1 = true };
        chip.Clk = true;
        chip.D1 = false;

        chip.Clk = false;

        await Assert.That(chip.Q1).IsEqualTo(true);
    }

    [Test]
    public async Task LatchesNewValueOnNextRisingEdge()
    {
        var chip = new Ttl74174Chip { D1 = true };
        chip.Clk = true;
        chip.Clk = false;

        chip.D1 = false;
        chip.Clk = true;

        await Assert.That(chip.Q1).IsEqualTo(false);
    }

    [Test]
    public async Task AsyncClearForcesAllOutputsLowRegardlessOfClock()
    {
        var chip = new Ttl74174Chip { D1 = true, D2 = true };
        chip.Clk = true;

        chip.Clr = false;

        await Assert.That(chip.Q1).IsEqualTo(false);
        await Assert.That(chip.Q2).IsEqualTo(false);
    }

    [Test]
    public async Task ClockIsIgnoredWhileClearIsAsserted()
    {
        var chip = new Ttl74174Chip { D1 = true, Clr = false };

        chip.Clk = true;

        await Assert.That(chip.Q1).IsEqualTo(false);
    }
}
