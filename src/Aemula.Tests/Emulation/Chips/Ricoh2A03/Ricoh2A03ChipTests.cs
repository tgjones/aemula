using System.IO;
using System.Threading.Tasks;

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

        var testHelper = new Ricoh2A03ChipTestHelper(
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
            });

        await testHelper.Startup();

        while (true)
        {
            await testHelper.Tick();

            if (testHelper.PC == 0xC66E)
            {
                break;
            }
        }

        await Assert.That(ram[0x0002]).IsEqualTo((byte)0x000);
        await Assert.That(ram[0x0003]).IsEqualTo((byte)0x000);
    }
}
