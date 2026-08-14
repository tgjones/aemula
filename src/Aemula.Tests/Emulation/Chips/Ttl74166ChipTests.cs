using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl74166ChipTests
{
    private static void Pulse(Ttl74166Chip chip)
    {
        chip.Clk = true;
        chip.Clk = false;
    }

    [Test]
    public async Task LoadsParallelDataOnRisingEdge()
    {
        var chip = new Ttl74166Chip
        {
            A = false,
            B = false,
            C = false,
            D = false,
            E = false,
            F = false,
            G = false,
            H = true,
            ShLd = false,
        };

        Pulse(chip);

        await Assert.That(chip.Qh).IsEqualTo(true);
        await Assert.That(chip.QhN).IsEqualTo(false);
    }

    [Test]
    public async Task SerialBitTakesEightShiftsToReachQh()
    {
        var chip = new Ttl74166Chip { ShLd = true };

        // Shift a single 1 bit in; it should take 8 shifts to walk from the
        // first stage to the last (Qh).
        chip.Ser = true;
        Pulse(chip);
        await Assert.That(chip.Qh).IsEqualTo(false);

        chip.Ser = false;
        for (var i = 0; i < 6; i++)
        {
            Pulse(chip);
        }
        await Assert.That(chip.Qh).IsEqualTo(false);

        Pulse(chip);
        await Assert.That(chip.Qh).IsEqualTo(true);

        Pulse(chip);
        await Assert.That(chip.Qh).IsEqualTo(false);
    }

    [Test]
    public async Task ClockInhibitBlocksShifting()
    {
        var chip = new Ttl74166Chip { ShLd = false, H = true };
        Pulse(chip);

        chip.ClkInh = true;
        chip.H = false;
        Pulse(chip);

        await Assert.That(chip.Qh).IsEqualTo(true);
    }

    [Test]
    public async Task AsyncClearForcesQhLowRegardlessOfClock()
    {
        var chip = new Ttl74166Chip { ShLd = false, H = true };
        Pulse(chip);

        chip.Clr = false;

        await Assert.That(chip.Qh).IsEqualTo(false);
    }

    [Test]
    public async Task ClockIsIgnoredWhileClearIsAsserted()
    {
        var chip = new Ttl74166Chip { ShLd = false, H = true, Clr = false };

        Pulse(chip);

        await Assert.That(chip.Qh).IsEqualTo(false);
    }
}
