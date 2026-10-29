using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Sn76489ChipTests
{
    private static void Write(Sn76489Chip chip, byte value)
    {
        chip.Data = value;
        chip.WeBar = false;
        chip.WeBar = true;
    }

    // One internal step is sixteen clock cycles.
    private static void Steps(Sn76489Chip chip, int steps)
    {
        for (var i = 0; i < steps * 16; i++)
        {
            chip.Clock = true;
            chip.Clock = false;
        }
    }

    private static int CountToggles(Sn76489Chip chip, int steps)
    {
        // The first step loads the counters; start counting after it.
        Steps(chip, 1);

        var toggles = 0;
        var last = chip.Output;

        for (var i = 0; i < steps; i++)
        {
            Steps(chip, 1);
            if (chip.Output != last)
            {
                toggles++;
                last = chip.Output;
            }
        }

        return toggles;
    }

    [Test]
    public async Task IsSilentAfterPowerOn()
    {
        var chip = new Sn76489Chip();
        Steps(chip, 100);

        await Assert.That(chip.Output).IsEqualTo(0f);
    }

    [Test]
    public async Task ToneTogglesOnceEveryPeriodSteps()
    {
        var chip = new Sn76489Chip();
        Write(chip, 0x90); // tone 0 full volume
        Write(chip, 0x80 | 0x05); // period low nibble 5
        Write(chip, 0x00); // period high 0 => 5

        await Assert.That(CountToggles(chip, 100)).IsEqualTo(20);
    }

    [Test]
    public async Task PeriodHighBitsComeFromSecondByte()
    {
        var chip = new Sn76489Chip();
        Write(chip, 0x90);
        Write(chip, 0x80 | 0x0F);
        Write(chip, 0x02); // 0x2F = 47

        await Assert.That(CountToggles(chip, 470)).IsEqualTo(10);
    }

    [Test]
    public async Task AttenuationIsTwoDecibelsPerStep()
    {
        var chip = new Sn76489Chip();
        Write(chip, 0x80 | 0x01);
        Write(chip, 0x90 | 0x05); // 10dB down

        Write(chip, 0x80 | 0x01); // period 1: toggles every step

        // Channels 1 and 2 are off, so only tone 0 contributes.
        var peak = 0f;
        for (var i = 0; i < 4; i++)
        {
            Steps(chip, 1);
            peak = System.Math.Max(peak, chip.Output);
        }

        await Assert.That(peak).IsEqualTo(0.316228f).Within(0.0001f);
    }

    [Test]
    public async Task AttenuationOfFifteenIsOff()
    {
        var chip = new Sn76489Chip();
        Write(chip, 0x90);
        Write(chip, 0x9F);

        Steps(chip, 10);
        await Assert.That(chip.Output).IsEqualTo(0f);
    }

    [Test]
    public async Task NoiseFollowsToneTwoWhenRateIsThree()
    {
        var chip = new Sn76489Chip();
        Write(chip, 0xF0); // noise full volume
        Write(chip, 0xE7); // white noise, rate from tone 2
        Write(chip, 0xC0 | 0x04); // tone 2 period 4 (silent: attenuation 15)

        // The register starts at 0x4000, so the first output bit is 0 and
        // changes only once tone 2's rising edges shift ones in.
        var toggles = CountToggles(chip, 2000);
        await Assert.That(toggles).IsGreaterThan(10);
    }

    [Test]
    public async Task DataIsIgnoredWhileChipEnableIsHigh()
    {
        var chip = new Sn76489Chip { CeBar = true };
        Write(chip, 0x90);
        Write(chip, 0x81);

        Steps(chip, 10);
        await Assert.That(chip.Output).IsEqualTo(0f);
    }
}
