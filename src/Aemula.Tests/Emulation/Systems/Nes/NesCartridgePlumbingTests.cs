using System;
using System.IO;
using System.Threading.Tasks;
using Aemula.Emulation.Systems.Nes;
using Aemula.Emulation.Systems.Nes.Mappers;

namespace Aemula.Tests.Emulation.Systems.Nes;

// Phase 0 of docs/nes-ppu-plan.md: the pin-level Cartridge / Mapper refactor.
// These pin down the cartridge bus mechanics without running a whole test ROM -
// NROM-256 PRG no longer mirrors, CHR RAM is writable, $6000-$7FFF WRAM exists,
// and name-table mirroring is the mapper driving CIRAM A10.
public class NesCartridgePlumbingTests
{
    // Minimal iNES image: 16-byte header, then PRG, then CHR.
    private static byte[] BuildInes(
        int prg16kUnits, int chr8kUnits, int mapper, bool verticalMirroring,
        Action<byte[]>? fillPrg = null)
    {
        var prg = new byte[prg16kUnits * 0x4000];
        var chr = new byte[chr8kUnits * 0x2000];
        fillPrg?.Invoke(prg);

        // A sane reset vector ($8000) so a loaded cartridge that gets ticked
        // doesn't wander - the header tests never tick, but the smoke test does.
        prg[^4] = 0x00;
        prg[^3] = 0x80;

        var header = new byte[16];
        header[0] = (byte)'N';
        header[1] = (byte)'E';
        header[2] = (byte)'S';
        header[3] = 0x1A;
        header[4] = (byte)prg16kUnits;
        header[5] = (byte)chr8kUnits;
        header[6] = (byte)((verticalMirroring ? 0x01 : 0x00) | ((mapper & 0x0F) << 4));
        header[7] = (byte)(mapper & 0xF0);

        var image = new byte[header.Length + prg.Length + chr.Length];
        header.CopyTo(image, 0);
        prg.CopyTo(image, header.Length);
        chr.CopyTo(image, header.Length + prg.Length);
        return image;
    }

    private static string WriteTempRom(byte[] image)
    {
        var path = Path.Combine(Path.GetTempPath(), $"aemula-nes-test-{Guid.NewGuid():N}.nes");
        File.WriteAllBytes(path, image);
        return path;
    }

    private static NesSystem LoadImage(byte[] image)
    {
        var nes = new NesSystem { DecodeVideo = false };
        nes.LoadProgram(WriteTempRom(image));
        return nes;
    }

    [Test]
    public async Task Nrom128PrgIsMirroredAcross8000AndC000()
    {
        var nes = LoadImage(BuildInes(
            prg16kUnits: 1, chr8kUnits: 1, mapper: 0, verticalMirroring: false,
            fillPrg: prg => prg[0x0000] = 0x11));

        await Assert.That(nes.ReadByteDebug(0x8000)).IsEqualTo((byte)0x11);
        await Assert.That(nes.ReadByteDebug(0xC000)).IsEqualTo((byte)0x11);
    }

    [Test]
    public async Task Nrom256PrgIsNotMirrored()
    {
        var nes = LoadImage(BuildInes(
            prg16kUnits: 2, chr8kUnits: 1, mapper: 0, verticalMirroring: false,
            fillPrg: prg =>
            {
                prg[0x0000] = 0x11; // -> CPU $8000
                prg[0x4000] = 0x22; // -> CPU $C000
            }));

        await Assert.That(nes.ReadByteDebug(0x8000)).IsEqualTo((byte)0x11);
        await Assert.That(nes.ReadByteDebug(0xC000)).IsEqualTo((byte)0x22);
    }

    [Test]
    public async Task CartridgeWramReadsBackWhatWasWritten()
    {
        var nes = LoadImage(BuildInes(1, 1, 0, false));

        nes.WriteByteDebug(0x6000, 0x5A);
        nes.WriteByteDebug(0x7FFF, 0xA5);

        await Assert.That(nes.ReadByteDebug(0x6000)).IsEqualTo((byte)0x5A);
        await Assert.That(nes.ReadByteDebug(0x7FFF)).IsEqualTo((byte)0xA5);
    }

    [Test]
    public async Task LoadingNestestStillRunsAfterTheRefactor()
    {
        // nestest.nes is NROM-128; this is the regression guard for moving the
        // cartridge bus onto the connector pins.
        var nes = new NesSystem { DecodeVideo = false };
        nes.LoadProgram(Path.Combine("Emulation", "Systems", "Nes", "Assets", "nestest.nes"));

        var startFrame = nes.Ppu.Frames;
        while (nes.Ppu.Frames - startFrame < 3)
        {
            nes.Tick();
        }

        await Assert.That(nes.Ppu.Frames - startFrame).IsGreaterThanOrEqualTo(3UL);
    }

    // ---- Mapper strategy, in isolation -----------------------------------

    private static MapperConfig Config(
        NametableMirroring mirroring, int prgBytes = 0x4000, bool chrRam = true) =>
        new(new byte[prgBytes], new byte[0x2000], chrRam, mirroring, HeaderHasPrgRam: false);

    [Test]
    public async Task HorizontalMirroringSelectsPageByPpuA11()
    {
        var m = Mapper.Create(0, Config(NametableMirroring.Horizontal));

        // $2000 == $2400 (lower page); $2800 == $2C00 (upper page).
        await Assert.That(m.CiramA10(0x2000)).IsFalse();
        await Assert.That(m.CiramA10(0x2400)).IsFalse();
        await Assert.That(m.CiramA10(0x2800)).IsTrue();
        await Assert.That(m.CiramA10(0x2C00)).IsTrue();
    }

    [Test]
    public async Task VerticalMirroringSelectsPageByPpuA10()
    {
        var m = Mapper.Create(0, Config(NametableMirroring.Vertical));

        // $2000 == $2800 (lower page); $2400 == $2C00 (upper page).
        await Assert.That(m.CiramA10(0x2000)).IsFalse();
        await Assert.That(m.CiramA10(0x2400)).IsTrue();
        await Assert.That(m.CiramA10(0x2800)).IsFalse();
        await Assert.That(m.CiramA10(0x2C00)).IsTrue();
    }

    [Test]
    public async Task ChrRamIsWritableAndChrRomIsNot()
    {
        var ram = Mapper.Create(0, Config(NametableMirroring.Vertical, chrRam: true));
        ram.ChrWrite(0x0000, 0xAB);
        await Assert.That(ram.ChrRead(0x0000)).IsEqualTo((byte)0xAB);

        var rom = Mapper.Create(0, Config(NametableMirroring.Vertical, chrRam: false));
        rom.ChrWrite(0x0000, 0xAB);
        await Assert.That(rom.ChrRead(0x0000)).IsEqualTo((byte)0x00);
    }

    [Test]
    public async Task Mapper000WramReadsBackThroughTheCpuBus()
    {
        var m = Mapper.Create(0, Config(NametableMirroring.Vertical));

        // $6000-$7FFF is driven by the board; $5FFF is not (open bus -> null).
        m.CpuWrite(0x6000, 0x5A);
        m.CpuWrite(0x7FFF, 0xA5);

        await Assert.That(m.CpuRead(0x6000)).IsEqualTo((byte?)0x5A);
        await Assert.That(m.CpuRead(0x7FFF)).IsEqualTo((byte?)0xA5);
        await Assert.That(m.CpuRead(0x5FFF)).IsNull();
    }

    [Test]
    public async Task MapperCreateThrowsForUnimplementedNumbers()
    {
        await Assert.That(() => Mapper.Create(1, Config(NametableMirroring.Vertical)))
            .ThrowsExactly<NotSupportedException>();
        await Assert.That(() => Mapper.Create(99, Config(NametableMirroring.Vertical)))
            .ThrowsExactly<NotSupportedException>();
    }
}
