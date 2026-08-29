using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ne555ChipTests
{
    private static void Trigger(Ne555Chip chip)
    {
        chip.TriggerBar = true;
        chip.TriggerBar = false;
    }

    [Test]
    public async Task OutputIsLowUntilTriggered()
    {
        var chip = new Ne555Chip { PulseTicks = 4 };

        chip.Tick();
        chip.Tick();

        await Assert.That(chip.Out).IsFalse();
    }

    [Test]
    public async Task TriggerDrivesOutputHighForPulseTicksThenLow()
    {
        var chip = new Ne555Chip { PulseTicks = 3 };

        Trigger(chip);
        await Assert.That(chip.Out).IsTrue();

        chip.Tick();
        chip.Tick();
        await Assert.That(chip.Out).IsTrue();

        chip.Tick();
        await Assert.That(chip.Out).IsFalse();
    }

    [Test]
    public async Task OutputStaysLowAfterTimeoutWithNoRetrigger()
    {
        var chip = new Ne555Chip { PulseTicks = 2 };

        Trigger(chip);
        chip.Tick();
        chip.Tick();
        await Assert.That(chip.Out).IsFalse();

        for (var i = 0; i < 10; i++)
        {
            chip.Tick();
        }

        await Assert.That(chip.Out).IsFalse();
    }

    [Test]
    public async Task FallingEdgeRetriggersAndRestartsTheOnTime()
    {
        var chip = new Ne555Chip { PulseTicks = 4 };

        Trigger(chip);
        chip.Tick();
        chip.Tick();
        chip.Tick();

        // Retrigger with one tick still to go: the on-time restarts from zero.
        Trigger(chip);

        for (var i = 0; i < 3; i++)
        {
            chip.Tick();
        }

        await Assert.That(chip.Out).IsTrue();

        chip.Tick();
        await Assert.That(chip.Out).IsFalse();
    }

    [Test]
    public async Task RisingEdgeOnTriggerDoesNothing()
    {
        var chip = new Ne555Chip { PulseTicks = 4 };

        // No high-to-low transition, so no pulse.
        chip.TriggerBar = true;

        await Assert.That(chip.Out).IsFalse();
    }

    [Test]
    public async Task ResetForcesOutputLowMidPulse()
    {
        var chip = new Ne555Chip { PulseTicks = 100 };

        Trigger(chip);
        chip.Tick();
        await Assert.That(chip.Out).IsTrue();

        chip.ResetBar = false;
        await Assert.That(chip.Out).IsFalse();
    }

    [Test]
    public async Task TriggerIsIgnoredWhileResetIsHeldLow()
    {
        var chip = new Ne555Chip { PulseTicks = 4, ResetBar = false };

        Trigger(chip);
        await Assert.That(chip.Out).IsFalse();

        // Releasing reset and triggering again works normally.
        chip.ResetBar = true;
        Trigger(chip);
        await Assert.That(chip.Out).IsTrue();
    }
}
