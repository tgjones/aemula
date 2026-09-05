using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Signetics2504ChipTests
{
    private static void Pulse(Signetics2504Chip chip)
    {
        chip.Phi2 = false;
        chip.Phi2 = true;
    }

    [Test]
    public async Task OutIsFalseBeforeAnyClocking()
    {
        var chip = new Signetics2504Chip();

        await Assert.That(chip.Out).IsFalse();
    }

    [Test]
    public async Task BitTakes1024ClocksToReachOut()
    {
        var chip = new Signetics2504Chip { In = true };
        Pulse(chip);
        chip.In = false;

        for (var i = 0; i < 1023; i++)
        {
            await Assert.That(chip.Out).IsFalse();
            Pulse(chip);
        }

        await Assert.That(chip.Out).IsTrue();
    }

    [Test]
    public async Task RecirculatingABitReturnsEvery1024Clocks()
    {
        var chip = new Signetics2504Chip { In = true };
        Pulse(chip);
        chip.In = false;

        var sightings = 0;

        for (var i = 0; i < 1024 * 3; i++)
        {
            chip.In = chip.Out; // recirculate
            Pulse(chip);

            if (chip.Out)
            {
                sightings++;
            }
        }

        await Assert.That(sightings).IsEqualTo(3);
    }

    [Test]
    public async Task PhiOnlyShiftsOnRisingEdge()
    {
        var chip = new Signetics2504Chip { In = true };

        chip.Phi2 = false;
        chip.Phi2 = true; // rising edge: shifts once
        chip.Phi2 = true; // still high: no second shift

        chip.In = false;

        for (var i = 0; i < 1022; i++)
        {
            Pulse(chip);
        }

        // Exactly one shift happened above despite two Phi2=true
        // assignments - if the second one had also shifted, the bit set by
        // In=true would already be one position further along than this
        // loop accounts for, and Out would be true a clock early.
        await Assert.That(chip.Out).IsFalse();
        Pulse(chip);
        await Assert.That(chip.Out).IsTrue();
    }
}
