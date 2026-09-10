using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Aemula.Emulation.Output;
using Aemula.Emulation.Peripherals;
using Aemula.Emulation.Systems;
using Aemula.Emulation.Systems.AppleI;
using Aemula.Emulation.Peripherals.Cassette;

namespace Aemula.Tests.Emulation.Systems;

public class RigBuilderTests
{
    private sealed class FakeDevice : IPeripheral
    {
        public bool Attached;
        public string Name => "Fake";
        public IReadOnlyList<ConsoleControl> Controls => [];
        public IAudioSource? Audio => null;
        public void Reset() { }
        public void Dispose() { }
    }

    private sealed class FakeSystem : EmulatedSystem
    {
        private readonly FakeDevice _device = new();
        public override ulong CyclesPerSecond => 1_000_000;
        public override void Tick() { }

        public override IReadOnlyList<PeripheralRequest> PeripheralRequests =>
        [
            PeripheralRequest.For<FakeDevice>(_ => _device, d => d.Attached = true),
        ];
    }

    [Test]
    public async Task BuildCreatesEachDeclaredPeripheralAndRunsItsAttach()
    {
        var rig = RigBuilder.Build(_ => new FakeSystem(), ExpansionSlotConfiguration.Empty, []);

        var device = rig.GetPeripheral<FakeDevice>();
        await Assert.That(device).IsNotNull();
        await Assert.That(device!.Attached).IsTrue();
    }

    [Test]
    public async Task BuildPassesTheResolvedSlotConfigurationToTheFactory()
    {
        var slots = new ExpansionSlot[]
        {
            new("s", "S", [new ExpansionCardOption("c", "C")], DefaultCardId: "c"),
        };

        string? seen = null;
        RigBuilder.Build(
            config => { seen = config["s"]; return new FakeSystem(); },
            ExpansionSlotConfiguration.Empty,
            slots);

        await Assert.That(seen).IsEqualTo("c");
    }

    [Test]
    public async Task AppleIDefaultsFitTheCassetteInterfaceAndCableItsDeck()
    {
        var rig = EmulatedSystems.FindById("applei")!.Build(ExpansionSlotConfiguration.Empty);

        await Assert.That(((AppleISystem)rig.System).HasCassetteInterface).IsTrue();
        await Assert.That(rig.GetPeripheral<CassetteDeck>()).IsNotNull();
    }

    [Test]
    public async Task AppleIWithAnEmptyExpansionSlotHasNoCassette()
    {
        var rig = EmulatedSystems.FindById("applei")!
            .Build(new ExpansionSlotConfiguration([("expansion", null)]));

        await Assert.That(((AppleISystem)rig.System).HasCassetteInterface).IsFalse();
        await Assert.That(rig.GetPeripheral<CassetteDeck>()).IsNull();
        await Assert.That(rig.Peripherals.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ASlotlessSystemBuildsWithNoPeripherals()
    {
        var rig = EmulatedSystems.FindById("spaceinvaders")!.Build(ExpansionSlotConfiguration.Empty);

        await Assert.That(rig.Peripherals.Count).IsEqualTo(0);
    }
}
