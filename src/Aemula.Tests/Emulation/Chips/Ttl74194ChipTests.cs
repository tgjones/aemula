using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl74194ChipTests
{
    private static void Pulse(Ttl74194Chip chip)
    {
        chip.Clk = true;
        chip.Clk = false;
    }

    [Test]
    public async Task LoadsParallelDataOnRisingEdge()
    {
        var chip = new Ttl74194Chip
        {
            A = true,
            B = false,
            C = true,
            D = true,
            S0 = true,
            S1 = true,
        };

        Pulse(chip);

        await Assert.That(chip.Qa).IsEqualTo(true);
        await Assert.That(chip.Qb).IsEqualTo(false);
        await Assert.That(chip.Qc).IsEqualTo(true);
        await Assert.That(chip.Qd).IsEqualTo(true);
    }

    [Test]
    public async Task ShiftsRightFromDsr()
    {
        var chip = new Ttl74194Chip { A = true, B = true, C = true, D = true, S0 = true, S1 = true };
        Pulse(chip);

        chip.S0 = true;
        chip.S1 = false;
        chip.Dsr = true;
        Pulse(chip);

        await Assert.That(chip.Qa).IsEqualTo(true);
        await Assert.That(chip.Qb).IsEqualTo(true);
        await Assert.That(chip.Qc).IsEqualTo(true);
        await Assert.That(chip.Qd).IsEqualTo(true);

        chip.Dsr = false;
        Pulse(chip);

        await Assert.That(chip.Qa).IsEqualTo(false);
        await Assert.That(chip.Qb).IsEqualTo(true);
        await Assert.That(chip.Qc).IsEqualTo(true);
        await Assert.That(chip.Qd).IsEqualTo(true);
    }

    [Test]
    public async Task ShiftsLeftFromDsl()
    {
        var chip = new Ttl74194Chip { A = false, B = false, C = false, D = false, S0 = true, S1 = true };
        Pulse(chip);

        chip.S0 = false;
        chip.S1 = true;
        chip.Dsl = true;
        Pulse(chip);

        await Assert.That(chip.Qa).IsEqualTo(false);
        await Assert.That(chip.Qb).IsEqualTo(false);
        await Assert.That(chip.Qc).IsEqualTo(false);
        await Assert.That(chip.Qd).IsEqualTo(true);

        chip.Dsl = false;
        Pulse(chip);

        await Assert.That(chip.Qa).IsEqualTo(false);
        await Assert.That(chip.Qb).IsEqualTo(false);
        await Assert.That(chip.Qc).IsEqualTo(true);
        await Assert.That(chip.Qd).IsEqualTo(false);
    }

    [Test]
    public async Task HoldsWhenBothModeSelectsAreLow()
    {
        var chip = new Ttl74194Chip { A = true, S0 = true, S1 = true };
        Pulse(chip);

        chip.S0 = false;
        chip.S1 = false;
        Pulse(chip);

        await Assert.That(chip.Qa).IsEqualTo(true);
    }

    [Test]
    public async Task AsyncClearForcesAllOutputsLowRegardlessOfClock()
    {
        var chip = new Ttl74194Chip { A = true, S0 = true, S1 = true };
        Pulse(chip);

        chip.Clr = false;

        await Assert.That(chip.Qa).IsEqualTo(false);
    }

    [Test]
    public async Task ClockIsIgnoredWhileClearIsAsserted()
    {
        var chip = new Ttl74194Chip { A = true, S0 = true, S1 = true, Clr = false };

        Pulse(chip);

        await Assert.That(chip.Qa).IsEqualTo(false);
    }
}
