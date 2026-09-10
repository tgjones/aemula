using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Aemula;
using Aemula.Emulation.Output.Wav;
using Aemula.Emulation.Peripherals.Cassette;
using Aemula.Emulation.Systems;
using Aemula.Emulation.Systems.Atari2600;

namespace Aemula.Tests.Emulation.Systems;

// The generic media-bay seam: MediaImage, the Rig's aggregation of the system's
// own bays with every peripheral's, routing an insert/eject to the owning host,
// and the power-on reset rule for a cartridge console.
public class MediaBayTests
{
    // ---- MediaImage ------------------------------------------------------

    [Test]
    public async Task FromFileKeepsTheFilenameAndYieldsTheBytes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aemula-media-{Guid.NewGuid():N}.bin");
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        await File.WriteAllBytesAsync(path, bytes);
        try
        {
            var image = MediaImage.FromFile(path);

            await Assert.That(image.Name).IsEqualTo(Path.GetFileName(path));

            using var stream = image.OpenRead();
            var read = new byte[bytes.Length];
            stream.ReadExactly(read);
            await Assert.That(read).IsEquivalentTo(bytes);
            await Assert.That(image.Data.Length).IsEqualTo(bytes.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- Rig aggregation ----------------------------------------------------

    [Test]
    public async Task ACartridgeSystemExposesExactlyItsOwnBay()
    {
        using var rig = EmulatedSystems.FindById("nes")!.Build(ExpansionSlotConfiguration.Empty);

        await Assert.That(rig.MediaBays.Count).IsEqualTo(1);
        await Assert.That(rig.MediaBays[0].Id).IsEqualTo("cartridge");
        await Assert.That(rig.MediaBays[0].Required).IsTrue();
    }

    [Test]
    public async Task TheAppleIWithItsCassetteCardExposesTheDecksBayAndNothingElse()
    {
        using var rig = EmulatedSystems.FindById("applei")!.Build(ExpansionSlotConfiguration.Empty);

        await Assert.That(rig.MediaBays.Count).IsEqualTo(1);
        await Assert.That(rig.MediaBays[0].Id).IsEqualTo("cassette");
    }

    [Test]
    public async Task TheAppleIWithAnEmptyExpansionSlotExposesNoBays()
    {
        using var rig = EmulatedSystems.FindById("applei")!
            .Build(new ExpansionSlotConfiguration([("expansion", null)]));

        await Assert.That(rig.MediaBays.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ASystemWithFixedBootRomsExposesNoBays()
    {
        using var rig = EmulatedSystems.FindById("spaceinvaders")!.Build(ExpansionSlotConfiguration.Empty);

        await Assert.That(rig.MediaBays.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ABayIdContributedByBothTheSystemAndAPeripheralThrowsInTheRigConstructor()
    {
        var system = new BayNamingSystem("cassette");
        var deck = new CassetteDeck(1000.0);

        await Assert.That(() => new Rig(system, [deck]))
            .ThrowsExactly<InvalidOperationException>();
    }

    // ---- routing ----------------------------------------------------------

    [Test]
    public async Task InsertReachesTheSystemAndEjectClearsIt()
    {
        using var rig = EmulatedSystems.FindById("atari2600")!.Build(ExpansionSlotConfiguration.Empty);
        var system = (Atari2600System)rig.System;

        rig.InsertMedia("cartridge", JmpSelfCartridge(0x1000));
        await Assert.That(system.ReadByteDebug(0x1000)).IsEqualTo((byte)0x4C);

        rig.EjectMedia("cartridge");
        await Assert.That(system.ReadByteDebug(0x1000)).IsEqualTo((byte)0x00);
    }

    [Test]
    public async Task InsertReachesTheCassetteDeck()
    {
        using var rig = EmulatedSystems.FindById("applei")!.Build(ExpansionSlotConfiguration.Empty);
        var deck = rig.GetPeripheral<CassetteDeck>()!;

        using var wav = new MemoryStream();
        WavWriter.Write(wav, new float[2000], sampleRate: 8000);

        rig.InsertMedia("cassette", MediaImage.FromBytes("tape.wav", wav.ToArray()));

        await Assert.That(deck.HasTape).IsTrue();
    }

    [Test]
    public async Task AnUnknownBayIdThrows()
    {
        using var rig = EmulatedSystems.FindById("nes")!.Build(ExpansionSlotConfiguration.Empty);

        await Assert.That(() => rig.InsertMedia("floppy", MediaImage.FromBytes("x", [0])))
            .ThrowsExactly<ArgumentException>();
        await Assert.That(() => rig.EjectMedia("floppy")).ThrowsExactly<ArgumentException>();
    }

    // ---- reset rule -----------------------------------------------------

    [Test]
    public async Task InsertingACartridgeBeforeTheFirstTickLetsTheCpuBoot()
    {
        var system = new Atari2600System();
        system.InsertMedia("cartridge", JmpSelfCartridge(0x1000));

        for (var i = 0; i < 50_000; i++)
        {
            system.Tick();
        }

        // Settled into the cartridge's JMP-to-self at $1000 - it took the
        // cartridge's reset vector, so the power-on reset completed on insert.
        await Assert.That(system.Cpu.Address).IsBetween((ushort)0x1000, (ushort)0x1002);
    }

    [Test]
    public async Task InsertingACartridgeAfterTickingHasBegunDoesNotPulseReset()
    {
        var system = new Atari2600System();

        // Boot cartridge A: CPU ends up looping at $1000.
        system.InsertMedia("cartridge", JmpSelfCartridge(0x1000));
        system.RunForDuration(TimeSpan.FromMilliseconds(1));
        await Assert.That(system.TotalCycles).IsGreaterThan(0UL);
        await Assert.That(system.Cpu.Address).IsBetween((ushort)0x1000, (ushort)0x1002);

        // Swap in cartridge B - same $1000 JMP-to-self bytes, but its reset
        // vector points at $1500 (also a JMP-to-self). No reset pulse this
        // time, so the CPU keeps running the $1000 loop and never takes B's
        // vector.
        system.InsertMedia("cartridge", TwoLoopCartridge(secondLoop: 0x1500));
        for (var i = 0; i < 5_000; i++)
        {
            system.Tick();
        }

        await Assert.That(system.Cpu.Address).IsBetween((ushort)0x1000, (ushort)0x1002);
    }

    // ---- helpers --------------------------------------------------------

    // A 2K cartridge: JMP-to-self at $1000, reset vector -> resetTarget.
    private static MediaImage JmpSelfCartridge(ushort resetTarget)
    {
        var rom = new byte[2048];
        rom[0] = 0x4C; rom[1] = 0x00; rom[2] = 0x10;     // $1000: JMP $1000
        rom[0x7FC] = (byte)(resetTarget & 0xFF);
        rom[0x7FD] = (byte)(resetTarget >> 8);
        return MediaImage.FromBytes("cart.bin", rom);
    }

    // A 2K cartridge with two JMP-to-self loops: one at $1000, one at
    // secondLoop; the reset vector points at secondLoop. (Cartridge2K mirrors
    // its 2K image across the 4K window, so $1nnn masks to offset $nnn.)
    private static MediaImage TwoLoopCartridge(ushort secondLoop)
    {
        var rom = new byte[2048];
        rom[0] = 0x4C; rom[1] = 0x00; rom[2] = 0x10;     // $1000: JMP $1000
        var off = secondLoop & 0x7FF;
        rom[off] = 0x4C;
        rom[off + 1] = (byte)(secondLoop & 0xFF);
        rom[off + 2] = (byte)(secondLoop >> 8);
        rom[0x7FC] = (byte)(secondLoop & 0xFF);
        rom[0x7FD] = (byte)(secondLoop >> 8);
        return MediaImage.FromBytes("cart.bin", rom);
    }

    // A bare system that claims one media bay of a given id, to force a
    // collision with a peripheral that claims the same one.
    private sealed class BayNamingSystem(string bayId) : EmulatedSystem
    {
        private readonly MediaBay _bay = new(bayId, bayId, false, [], "");

        public override ulong CyclesPerSecond => 1_000_000;
        public override void Tick() { }
        public override IReadOnlyList<MediaBay> MediaBays => [_bay];
    }
}
