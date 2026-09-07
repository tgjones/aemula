using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Lm311ChipTests
{
    [Test]
    public async Task StartsLow()
    {
        var chip = new Lm311Chip();

        await Assert.That(chip.Out).IsEqualTo(false);
    }

    [Test]
    public async Task GoesHighOnceInputRisesPastUpperThreshold()
    {
        var chip = new Lm311Chip(hysteresis: 0.1);

        chip.Input = 0.05;
        await Assert.That(chip.Out).IsEqualTo(false);

        chip.Input = 0.2;
        await Assert.That(chip.Out).IsEqualTo(true);
    }

    [Test]
    public async Task GoesLowOnceInputFallsPastLowerThreshold()
    {
        var chip = new Lm311Chip(hysteresis: 0.1);
        chip.Input = 1.0;

        chip.Input = -0.05;
        await Assert.That(chip.Out).IsEqualTo(true);

        chip.Input = -0.2;
        await Assert.That(chip.Out).IsEqualTo(false);
    }

    [Test]
    public async Task HoldsStateWithinTheDeadBand()
    {
        var chip = new Lm311Chip(hysteresis: 0.1);

        chip.Input = 0.5;
        await Assert.That(chip.Out).IsEqualTo(true);

        // A full swing through the dead band without crossing either threshold
        // must not disturb the latched output.
        chip.Input = 0.09;
        chip.Input = -0.09;
        chip.Input = 0.0;
        await Assert.That(chip.Out).IsEqualTo(true);
    }

    [Test]
    public async Task SmallNoiseAroundZeroDoesNotChatter()
    {
        var chip = new Lm311Chip(hysteresis: 0.05);
        var transitions = 0;
        var last = chip.Out;

        // A clean sine well above the dead band, plus noise smaller than it,
        // should produce exactly two transitions per cycle.
        for (var i = 0; i < 2000; i++)
        {
            var phase = i * 0.05;
            var signal = 0.6 * System.Math.Sin(phase);
            var noise = 0.03 * System.Math.Sin(phase * 37.0);
            chip.Input = signal + noise;

            if (chip.Out != last)
            {
                transitions++;
                last = chip.Out;
            }
        }

        var cycles = 2000 * 0.05 / (2.0 * System.Math.PI);
        await Assert.That(transitions).IsBetween((int)(cycles * 2) - 2, (int)(cycles * 2) + 2);
    }

    [Test]
    public async Task ResetReturnsToPowerOnState()
    {
        var chip = new Lm311Chip();
        chip.Input = 1.0;
        await Assert.That(chip.Out).IsEqualTo(true);

        chip.Reset();

        await Assert.That(chip.Out).IsEqualTo(false);
    }
}
