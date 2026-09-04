using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl74123ChipTests
{
    private static void TriggerViaABar(Ttl74123Chip chip)
    {
        chip.ABar1 = true;
        chip.ABar1 = false;
    }

    [Test]
    public async Task FallingEdgeOnABarTriggersWhenBAndClrAreHigh()
    {
        var chip = new Ttl74123Chip { PulseTicks1 = 4 };
        chip.B1 = true;

        TriggerViaABar(chip);

        await Assert.That(chip.Q1).IsTrue();
        await Assert.That(chip.Qn1).IsFalse();
    }

    [Test]
    public async Task ABarHighInhibitsRegardlessOfB()
    {
        var chip = new Ttl74123Chip { PulseTicks1 = 4 };
        chip.B1 = true;
        chip.ABar1 = true;

        await Assert.That(chip.Q1).IsFalse();
    }

    [Test]
    public async Task RisingEdgeOnBTriggersWhenABarIsLowAndClrIsHigh()
    {
        var chip = new Ttl74123Chip { PulseTicks1 = 4 };
        chip.ABar1 = false;

        chip.B1 = false;
        chip.B1 = true;

        await Assert.That(chip.Q1).IsTrue();
    }

    [Test]
    public async Task BLowInhibitsRegardlessOfABar()
    {
        var chip = new Ttl74123Chip { PulseTicks1 = 4 };
        chip.ABar1 = false;
        chip.B1 = false;

        await Assert.That(chip.Q1).IsFalse();
    }

    [Test]
    public async Task RisingEdgeOnClrTriggersWhenABarLowAndBHigh()
    {
        var chip = new Ttl74123Chip { PulseTicks1 = 4 };
        chip.ABar1 = false;
        chip.B1 = true;
        chip.Clr1 = false;

        chip.Clr1 = true;

        await Assert.That(chip.Q1).IsTrue();
    }

    [Test]
    public async Task ClrLowForcesOutputLowImmediately()
    {
        var chip = new Ttl74123Chip { PulseTicks1 = 100 };
        chip.B1 = true;
        TriggerViaABar(chip);
        await Assert.That(chip.Q1).IsTrue();

        chip.Clr1 = false;

        await Assert.That(chip.Q1).IsFalse();
        await Assert.That(chip.Qn1).IsTrue();
    }

    [Test]
    public async Task OutputGoesLowAfterPulseTicksElapse()
    {
        var chip = new Ttl74123Chip { PulseTicks1 = 3 };
        chip.B1 = true;
        TriggerViaABar(chip);

        chip.Tick();
        chip.Tick();
        await Assert.That(chip.Q1).IsTrue();

        chip.Tick();
        await Assert.That(chip.Q1).IsFalse();
        await Assert.That(chip.Qn1).IsTrue();
    }

    [Test]
    public async Task RetriggeringMidPulseRestartsTheOnTime()
    {
        var chip = new Ttl74123Chip { PulseTicks1 = 4 };
        chip.B1 = true;
        TriggerViaABar(chip);

        chip.Tick();
        chip.Tick();
        chip.Tick();

        // Retrigger with one tick still to go: the on-time restarts from zero.
        TriggerViaABar(chip);

        for (var i = 0; i < 3; i++)
        {
            chip.Tick();
        }

        await Assert.That(chip.Q1).IsTrue();

        chip.Tick();
        await Assert.That(chip.Q1).IsFalse();
    }

    [Test]
    public async Task ChannelsAreIndependent()
    {
        var chip = new Ttl74123Chip { PulseTicks1 = 4, PulseTicks2 = 4 };
        chip.B1 = true;
        chip.B2 = true;

        TriggerViaABar(chip);

        await Assert.That(chip.Q1).IsTrue();
        await Assert.That(chip.Q2).IsFalse();
    }
}
