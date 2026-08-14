using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ay53600ChipTests
{
    // Matches Ay53600Chip's DebounceTicks constant: the number of Tick()
    // calls a held key needs before it latches and strobes.
    private const int DebounceTicks = 640;

    private static void TickN(Ay53600Chip chip, int n)
    {
        for (var i = 0; i < n; i++)
        {
            chip.Tick();
        }
    }

    [Test]
    public async Task NoKeyPressedNeverStrobes()
    {
        var chip = new Ay53600Chip();

        TickN(chip, 1000);

        await Assert.That(chip.AnyKeyDown).IsEqualTo(false);
        await Assert.That(chip.DataReady).IsEqualTo(false);
        await Assert.That(chip.Data).IsEqualTo((byte)0);
    }

    [Test]
    public async Task AnyKeyDownAssertsAsSoonAsAKeyIsPressed()
    {
        var chip = new Ay53600Chip();
        chip.SetPressedKey(1, 0); // Q

        chip.Tick();

        await Assert.That(chip.AnyKeyDown).IsEqualTo(true);
    }

    [Test]
    public async Task HeldKeyDoesNotStrobeBeforeDebounceCompletes()
    {
        var chip = new Ay53600Chip();
        chip.SetPressedKey(1, 0); // Q

        TickN(chip, DebounceTicks - 1);

        await Assert.That(chip.DataReady).IsEqualTo(false);
        await Assert.That(chip.Data).IsEqualTo((byte)0);
    }

    [Test]
    public async Task HeldKeyStrobesAndLatchesOnceDebounceCompletes()
    {
        var chip = new Ay53600Chip();
        chip.SetPressedKey(1, 0); // Q

        TickN(chip, DebounceTicks);

        await Assert.That(chip.DataReady).IsEqualTo(true);
        await Assert.That(chip.Data).IsEqualTo((byte)'Q');
    }

    [Test]
    public async Task StrobeIsOnlyAOneTickPulse()
    {
        var chip = new Ay53600Chip();
        chip.SetPressedKey(1, 0); // Q

        TickN(chip, DebounceTicks);
        chip.Tick();

        await Assert.That(chip.DataReady).IsEqualTo(false);
        // The latched code is still held even after the strobe pulse ends.
        await Assert.That(chip.Data).IsEqualTo((byte)'Q');
    }

    [Test]
    public async Task ReleasingKeyBeforeDebounceCompletesCancelsTheStrobe()
    {
        var chip = new Ay53600Chip();
        chip.SetPressedKey(1, 0); // Q

        TickN(chip, DebounceTicks / 2);
        chip.SetPressedKey(-1, -1);
        TickN(chip, 1000);

        await Assert.That(chip.AnyKeyDown).IsEqualTo(false);
        await Assert.That(chip.DataReady).IsEqualTo(false);
        await Assert.That(chip.Data).IsEqualTo((byte)0);
    }

    [Test]
    public async Task ChangingTheHeldKeyMidDebounceRestartsIt()
    {
        var chip = new Ay53600Chip();
        chip.SetPressedKey(1, 0); // Q
        TickN(chip, DebounceTicks / 2);

        chip.SetPressedKey(1, 1); // W
        TickN(chip, DebounceTicks + 9); // + worst-case scan latency to reacquire.

        // DataReady is a one-tick pulse, and the debounce restart plus the
        // scan-latency slack above means it will already have come and
        // gone by now - only the latched Data is checked here.
        await Assert.That(chip.Data).IsEqualTo((byte)'W');
    }

    [Test]
    public async Task ControlProducesTheStandardControlCodeForLetters()
    {
        var chip = new Ay53600Chip { Control = true };
        chip.SetPressedKey(1, 0); // Q

        TickN(chip, DebounceTicks);

        await Assert.That(chip.Data).IsEqualTo((byte)('Q' & 0x1F));
    }

    [Test]
    public async Task ShiftSelectsThePunctuationKeysSecondSymbol()
    {
        var chip = new Ay53600Chip { Shift = true };
        chip.SetPressedKey(0, 8); // The ":"/"*" key.

        // +9: worst-case scan latency for the X ring counter to first reach
        // this key's row (X=0), on top of the debounce delay itself.
        TickN(chip, DebounceTicks + 9);

        await Assert.That(chip.Data).IsEqualTo((byte)'*');
    }

    [Test]
    public async Task UnshiftedPunctuationKeyProducesItsFirstSymbol()
    {
        var chip = new Ay53600Chip();
        chip.SetPressedKey(0, 8); // The ":"/"*" key.

        TickN(chip, DebounceTicks + 9);

        await Assert.That(chip.Data).IsEqualTo((byte)':');
    }

    [Test]
    public async Task ReturnProducesAFixedCodeRegardlessOfModifiers()
    {
        var chip = new Ay53600Chip { Shift = true, Control = true };
        chip.SetPressedKey(4, 9); // Return.

        TickN(chip, DebounceTicks + 9);

        await Assert.That(chip.Data).IsEqualTo((byte)0x0D);
    }

    [Test]
    public async Task LeftArrowProducesCtrlH()
    {
        var chip = new Ay53600Chip();
        chip.SetPressedKey(2, 8); // Left arrow.

        TickN(chip, DebounceTicks + 9);

        await Assert.That(chip.Data).IsEqualTo((byte)0x08);
    }
}
