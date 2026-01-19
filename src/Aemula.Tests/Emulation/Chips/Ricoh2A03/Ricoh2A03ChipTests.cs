using System.IO;
using System.Threading.Tasks;
using Aemula.Emulation.Chips.Mos6502;
using Aemula.Tests.Emulation.Chips.Mos6502;

namespace Aemula.Tests.Emulation.Chips.Ricoh2A03;

public class Ricoh2A03ChipTests
{
    private static readonly string AssetsPath = Path.Combine("Emulation", "Chips", "Ricoh2A03", "Assets");

    [Test]
    public async Task NesTest()
    {
        byte[] rom;
        using (var reader = new BinaryReader(File.OpenRead(Path.Combine(AssetsPath, "nestest.nes"))))
        {
            reader.BaseStream.Seek(16, SeekOrigin.Current);
            rom = reader.ReadBytes(16384);
        }

        // Patch the test start address into the RESET vector.
        rom[0x3FFC] = 0x00;
        rom[0x3FFD] = 0xC0;

        var ram = new byte[0x0800];

        // APU and I/O registers - for the purposes of this test, treat them as RAM.
        var apu = new byte[0x18];

        var testHelper = new Mos6502ChipTestHelper(
            address => address switch
            {
                _ when address <= 0x1FFF => ram[address & 0x07FF],
                _ when address >= 0x4000 && address <= 0x4017 => apu[address - 0x4000],
                _ when address >= 0x8000 && address <= 0xFFFF => rom[address - 0x8000 & 0x3FFF],
                _ => rom[address - 0x4000]
            },
            (address, data) =>
            {
                switch (address)
                {
                    case var _ when address <= 0x1FFF:
                        ram[address & 0x07FF] = data;
                        break;

                    case var _ when address >= 0x4000 && address <= 0x4017:
                        apu[address - 0x4000] = data;
                        break;
                }
            },
            bcdEnabled: false,
            compatibilityMode: Mos6502CompatibilityMode.NesTest);

        await testHelper.Startup();

        testHelper.Chip.X = 0x00;
        testHelper.Chip.SP = 0x00;


        //var cpu = new Mos6502Chip(new Mos6502Options(bcdEnabled: false));
        //ref var pins = ref cpu.Pins;

        var cycles = 0;
        var shouldLog = false;

        using var referenceLogReader = new StreamReader(Path.Combine(AssetsPath, "nestest.log"));

        while (true)
        {
            await testHelper.Tick();

            if (testHelper.Chip.PC == 0xC000)
            {
                shouldLog = true;
                cycles = 0;
            }
            else if (testHelper.Chip.PC == 0xC66E)
            {
                break;
            }

            if (shouldLog && testHelper.Chip.Pins.Sync)
            {
                await Assert
                    .That($"{testHelper.PC:X4}  A:{testHelper.Chip.A:X2} X:{testHelper.Chip.X:X2} Y:{testHelper.Chip.Y:X2} P:{testHelper.Chip.P.AsByte(false):X2} SP:{testHelper.Chip.SP:X2} CPUC:{cycles}")
                    .IsEqualTo(referenceLogReader.ReadLine());
            }

            var address = testHelper.Chip.Pins.Address;

            if (testHelper.Chip.Pins.RW)
            {
                if (shouldLog)
                {
                    await Assert
                        .That($"      READ      ${address:X4} => ${testHelper.Chip.Pins.Data:X2}")
                        .IsEqualTo(referenceLogReader.ReadLine());
                }
            }
            else
            {
                if (shouldLog)
                {
                    await Assert
                        .That($"      WRITE     ${address:X4} <= ${testHelper.Chip.Pins.Data:X2}")
                        .IsEqualTo(referenceLogReader.ReadLine());
                }
            }

            cycles++;
        }

        await Assert.That(referenceLogReader.BaseStream.Position).IsEqualTo(referenceLogReader.BaseStream.Length);

        await Assert.That(ram[0x0002]).IsEqualTo((byte)0x000);
        await Assert.That(ram[0x0003]).IsEqualTo((byte)0x000);
    }
}
