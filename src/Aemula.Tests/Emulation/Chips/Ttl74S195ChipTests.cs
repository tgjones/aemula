using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl74S195ChipTests
{
    private static void Pulse(Ttl74S195Chip chip)
    {
        chip.Clk = true;
        chip.Clk = false;
    }

    [Test]
    public async Task LoadsParallelDataOnRisingEdgeWhenShLdIsLow()
    {
        var chip = new Ttl74S195Chip
        {
            A = true,
            B = false,
            C = true,
            D = true,
            ShLd = false,
        };

        Pulse(chip);

        await Assert.That(chip.Qa).IsEqualTo(true);
        await Assert.That(chip.Qb).IsEqualTo(false);
        await Assert.That(chip.Qc).IsEqualTo(true);
        await Assert.That(chip.Qd).IsEqualTo(true);
    }

    [Test]
    public async Task ShiftsRightWithSetOnJKn()
    {
        var chip = new Ttl74S195Chip { A = true, B = true, C = true, D = true, ShLd = false };
        Pulse(chip);

        chip.ShLd = true;
        chip.J = true;
        chip.Kn = true; // Set: Qa becomes 1.
        Pulse(chip);

        await Assert.That(chip.Qa).IsEqualTo(true);
        await Assert.That(chip.Qb).IsEqualTo(true);
        await Assert.That(chip.Qc).IsEqualTo(true);
        await Assert.That(chip.Qd).IsEqualTo(true);

        chip.J = false;
        chip.Kn = false; // Reset: Qa becomes 0.
        Pulse(chip);

        await Assert.That(chip.Qa).IsEqualTo(false);
        await Assert.That(chip.Qb).IsEqualTo(true);
        await Assert.That(chip.Qc).IsEqualTo(true);
        await Assert.That(chip.Qd).IsEqualTo(true);
    }

    [Test]
    public async Task HoldsQaWhenJIsLowAndKnIsHigh()
    {
        var chip = new Ttl74S195Chip { A = true, ShLd = false };
        Pulse(chip);

        chip.ShLd = true;
        chip.J = false;
        chip.Kn = true;
        Pulse(chip);

        await Assert.That(chip.Qa).IsEqualTo(true);
        await Assert.That(chip.Qb).IsEqualTo(true);
    }

    [Test]
    public async Task TogglesQaWhenJIsHighAndKnIsLow()
    {
        var chip = new Ttl74S195Chip { A = true, ShLd = false };
        Pulse(chip);

        chip.ShLd = true;
        chip.J = true;
        chip.Kn = false;
        Pulse(chip);

        await Assert.That(chip.Qa).IsEqualTo(false);

        Pulse(chip);

        await Assert.That(chip.Qa).IsEqualTo(true);
    }

    [Test]
    public async Task AsyncClearForcesAllOutputsLowRegardlessOfClock()
    {
        var chip = new Ttl74S195Chip { A = true, ShLd = false };
        Pulse(chip);

        chip.Clr = false;

        await Assert.That(chip.Qa).IsEqualTo(false);
        await Assert.That(chip.QdInverted).IsEqualTo(true);
    }

    [Test]
    public async Task ClockIsIgnoredWhileClearIsAsserted()
    {
        var chip = new Ttl74S195Chip { A = true, ShLd = false, Clr = false };

        Pulse(chip);

        await Assert.That(chip.Qa).IsEqualTo(false);
    }
}
