using System.Threading.Tasks;
using Aemula.Emulation.Chips;

namespace Aemula.Tests.Emulation.Chips;

public class Ttl7493ChipTests
{
    [Test]
    public async Task DivideBy2SectionTogglesQaOnClockFallingEdge()
    {
        var chip = new Ttl7493Chip { A = true };

        chip.A = false;

        await Assert.That(chip.QA).IsEqualTo(true);
    }

    [Test]
    public async Task DivideBy2SectionIgnoresClockRisingEdge()
    {
        var chip = new Ttl7493Chip();

        chip.A = true;

        await Assert.That(chip.QA).IsEqualTo(false);
    }

    [Test]
    public async Task DivideBy8SectionRipplesThroughAllThreeStages()
    {
        var chip = new Ttl7493Chip();

        // 8 falling edges on B should carry QB/QC/QD through a full cycle
        // (000 -> 111 -> 000), same as a real 3-bit ripple counter.
        for (var i = 0; i < 7; i++)
        {
            chip.B = true;
            chip.B = false;
        }

        await Assert.That(chip.QB).IsEqualTo(true);
        await Assert.That(chip.QC).IsEqualTo(true);
        await Assert.That(chip.QD).IsEqualTo(true);

        chip.B = true;
        chip.B = false;

        await Assert.That(chip.QB).IsEqualTo(false);
        await Assert.That(chip.QC).IsEqualTo(false);
        await Assert.That(chip.QD).IsEqualTo(false);
    }

    [Test]
    public async Task MasterResetRequiresBothR0PinsAsserted()
    {
        var chip = new Ttl7493Chip();
        chip.A = true;
        chip.A = false; // QA = true.

        chip.R01 = true;

        await Assert.That(chip.QA).IsEqualTo(true); // R02 not yet asserted.

        chip.R02 = true;

        await Assert.That(chip.QA).IsEqualTo(false);
    }

    [Test]
    public async Task ClockIsIgnoredWhileMasterResetAsserted()
    {
        var chip = new Ttl7493Chip { R01 = true, R02 = true };

        chip.A = true;
        chip.A = false;

        await Assert.That(chip.QA).IsEqualTo(false);
    }

    [Test]
    public async Task SectionsAreIndependent()
    {
        var chip = new Ttl7493Chip();

        chip.A = true;
        chip.A = false;

        await Assert.That(chip.QA).IsEqualTo(true);
        await Assert.That(chip.QB).IsEqualTo(false);
    }
}
