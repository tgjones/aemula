using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl7474ChipTests
{
    [Test]
    public async Task LatchesDOnRisingEdge()
    {
        var chip = new Ttl7474Chip { D1 = true };

        chip.Clk1 = true;

        await Assert.That(chip.Q1).IsEqualTo(true);
        await Assert.That(chip.Qn1).IsEqualTo(false);
    }

    [Test]
    public async Task IgnoresDWhileClockIsHigh()
    {
        var chip = new Ttl7474Chip { D1 = true };
        chip.Clk1 = true;

        chip.D1 = false;

        await Assert.That(chip.Q1).IsEqualTo(true);
    }

    [Test]
    public async Task IgnoresDOnFallingEdge()
    {
        var chip = new Ttl7474Chip { D1 = true };
        chip.Clk1 = true;
        chip.D1 = false;

        chip.Clk1 = false;

        await Assert.That(chip.Q1).IsEqualTo(true);
    }

    [Test]
    public async Task LatchesNewDOnNextRisingEdge()
    {
        var chip = new Ttl7474Chip { D1 = true };
        chip.Clk1 = true;
        chip.Clk1 = false;

        chip.D1 = false;
        chip.Clk1 = true;

        await Assert.That(chip.Q1).IsEqualTo(false);
        await Assert.That(chip.Qn1).IsEqualTo(true);
    }

    [Test]
    public async Task AsyncClearForcesQLowRegardlessOfClock()
    {
        var chip = new Ttl7474Chip { D1 = true };
        chip.Clk1 = true;

        chip.Clr1 = false;

        await Assert.That(chip.Q1).IsEqualTo(false);
        await Assert.That(chip.Qn1).IsEqualTo(true);
    }

    [Test]
    public async Task AsyncPresetForcesQHighRegardlessOfClock()
    {
        var chip = new Ttl7474Chip { D1 = false };
        chip.Clk1 = true;

        chip.Pre1 = false;

        await Assert.That(chip.Q1).IsEqualTo(true);
        await Assert.That(chip.Qn1).IsEqualTo(false);
    }

    [Test]
    public async Task BothAsyncInputsAssertedForcesBothOutputsHigh()
    {
        var chip = new Ttl7474Chip();

        chip.Clr1 = false;
        chip.Pre1 = false;

        await Assert.That(chip.Q1).IsEqualTo(true);
        await Assert.That(chip.Qn1).IsEqualTo(true);
    }

    [Test]
    public async Task ClockIsIgnoredWhileClearIsAsserted()
    {
        var chip = new Ttl7474Chip { D1 = true, Clr1 = false };

        chip.Clk1 = true;

        await Assert.That(chip.Q1).IsEqualTo(false);
    }

    [Test]
    public async Task FlipFlopsAreIndependent()
    {
        var chip = new Ttl7474Chip { D1 = true, D2 = false };

        chip.Clk1 = true;
        chip.Clk2 = true;

        await Assert.That(chip.Q1).IsEqualTo(true);
        await Assert.That(chip.Q2).IsEqualTo(false);
    }
}
