using System;
using System.Threading.Tasks;
using Aemula.Emulation.Systems;

namespace Aemula.Tests.Emulation.Systems;

public class ExpansionSlotConfigurationTests
{
    private static readonly ExpansionSlot[] Slots =
    [
        new ExpansionSlot(
            "expansion",
            "Expansion connector",
            [new ExpansionCardOption("aci", "Apple Cassette Interface"), new ExpansionCardOption("disk", "Disk")],
            DefaultCardId: "aci"),
        new ExpansionSlot("aux", "Aux connector", [new ExpansionCardOption("clock", "Clock")]),
    ];

    [Test]
    public async Task UnspecifiedSlotsTakeTheirDefault()
    {
        var resolved = ExpansionSlotConfiguration.Empty.ResolvedAgainst(Slots);

        await Assert.That(resolved["expansion"]).IsEqualTo("aci");
        await Assert.That(resolved["aux"]).IsNull();
    }

    [Test]
    public async Task AnExplicitChoiceWins()
    {
        var resolved = new ExpansionSlotConfiguration([("expansion", "disk")]).ResolvedAgainst(Slots);

        await Assert.That(resolved["expansion"]).IsEqualTo("disk");
    }

    [Test]
    public async Task NoneOverridesADefault()
    {
        var resolved = new ExpansionSlotConfiguration([("expansion", null)]).ResolvedAgainst(Slots);

        await Assert.That(resolved["expansion"]).IsNull();
        await Assert.That(resolved.HasChoiceFor("expansion")).IsTrue();
    }

    [Test]
    public async Task AnUnknownSlotIdThrows()
    {
        await Assert.That(() => new ExpansionSlotConfiguration([("nope", "aci")]).ResolvedAgainst(Slots))
            .ThrowsExactly<ArgumentException>();
    }

    [Test]
    public async Task ACardTheSlotDoesNotOfferThrows()
    {
        await Assert.That(() => new ExpansionSlotConfiguration([("expansion", "clock")]).ResolvedAgainst(Slots))
            .ThrowsExactly<ArgumentException>();
    }

    [Test]
    public async Task RepeatedSlotIdIsLastWins()
    {
        var config = new ExpansionSlotConfiguration([("expansion", "aci"), ("expansion", "disk")]);

        await Assert.That(config["expansion"]).IsEqualTo("disk");
    }

    [Test]
    public async Task WithFoldsInASingleChange()
    {
        var config = ExpansionSlotConfiguration.Empty.With("expansion", "disk").With("aux", "clock");
        var resolved = config.ResolvedAgainst(Slots);

        await Assert.That(resolved["expansion"]).IsEqualTo("disk");
        await Assert.That(resolved["aux"]).IsEqualTo("clock");
    }
}
