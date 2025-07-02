using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Aemula.Emulation.Chips.Mos6502;

namespace Aemula.Tests.Emulation.Chips.Mos6502;

public class Mos6502ChipTests
{
    private static readonly string AssetsPath = Path.Combine("Emulation", "Chips", "Mos6502", "Assets");

    [Test]
    public async Task AllSuiteA()
    {
        var rom = File.ReadAllBytes(Path.Combine(AssetsPath, "AllSuiteA.bin"));
        var ram = new byte[0x4000];

        var cpu = new Aemula.Emulation.Chips.Mos6502.Mos6502Chip(Mos6502Options.Default);

        ref var pins = ref cpu.Pins;

        while (cpu.PC != 0x45C2)
        {
            cpu.Tick();

            var address = pins.Address;

            if (pins.RW)
            {
                pins.Data = address switch
                {
                    _ when address <= 0x3FFF => ram[address],
                    _ => rom[address - 0x4000]
                };
            }
            else
            {
                if (address <= 0x3FFF)
                {
                    ram[address] = pins.Data;
                }
            }
        }

        await Assert.That(ram[0x0210]).IsEqualTo((byte)0xFF);
    }

    [Test]
    public async Task DormannFunctionalTest()
    {
        var ram = File.ReadAllBytes(Path.Combine(AssetsPath, "6502_functional_test.bin"));
        await Assert.That(ram.Length).IsEqualTo(0x10000);

        // Patch the test start address into the RESET vector.
        ram[0xFFFC] = 0x00;
        ram[0xFFFD] = 0x04;

        var cpu = new Aemula.Emulation.Chips.Mos6502.Mos6502Chip(Mos6502Options.Default);

        ref var pins = ref cpu.Pins;

        while (cpu.PC != 0x3399 && cpu.PC != 0xD0FE)
        {
            cpu.Tick();

            var address = pins.Address;

            if (pins.RW)
            {
                pins.Data = ram[address];
            }
            else
            {
                ram[address] = pins.Data;
            }
        }

        await Assert.That(cpu.PC).IsEqualTo((ushort)0x3399);
    }

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

        var cpu = new Aemula.Emulation.Chips.Mos6502.Mos6502Chip(new Mos6502Options(bcdEnabled: false));
        ref var pins = ref cpu.Pins;

        using (var streamWriter = new StreamWriter("nestest_aemula.log"))
        {
            var cycles = 0;
            var shouldLog = false;

            while (cpu.PC != 0xC66E)
            {
                cpu.Tick();

                cycles += 1;

                if (cycles == 7)
                {
                    shouldLog = true;
                }

                if (shouldLog && pins.Sync)
                {
                    streamWriter.WriteLine($"{cpu.PC:X4}  A:{cpu.A:X2} X:{cpu.X:X2} Y:{cpu.Y:X2} P:{cpu.P.AsByte(false):X2} SP:{cpu.SP:X2} CPUC:{cycles - 7}");
                }

                var address = pins.Address;

                if (pins.RW)
                {
                    pins.Data = address switch
                    {
                        _ when address <= 0x1FFF => ram[address & 0x07FF],
                        _ when address >= 0x4000 && address <= 0x4017 => apu[address - 0x4000],
                        _ when address >= 0x8000 && address <= 0xFFFF => rom[address - 0x8000 & 0x3FFF],
                        _ => rom[address - 0x4000]
                    };

                    if (shouldLog)
                    {
                        streamWriter.WriteLine($"      READ      ${address:X4} => ${pins.Data:X2}");
                    }
                }
                else
                {
                    switch (address)
                    {
                        case var _ when address <= 0x1FFF:
                            ram[address & 0x07FF] = pins.Data;
                            break;

                        case var _ when address >= 0x4000 && address <= 0x4017:
                            apu[address - 0x4000] = pins.Data;
                            break;
                    }

                    if (shouldLog)
                    {
                        streamWriter.WriteLine($"      WRITE     ${address:X4} <= ${pins.Data:X2}");
                    }
                }
            }

            streamWriter.Flush();
            streamWriter.Dispose();
        }

        await Assert.That(ram[0x0002]).IsEqualTo((byte)0x000);
        await Assert.That(ram[0x0003]).IsEqualTo((byte)0x000);

        await Assert
            .That(File.ReadAllText(Path.Combine(AssetsPath, "nestest.log")))
            .IsEqualTo(File.ReadAllText("nestest_aemula.log"));
    }

    [Test]
    public void C64Suite()
    {
        static string PetsciiToAscii(byte character) => character switch
        {
            147 => "\n------------\n", // Clear
            14 => "", // Toggle lowercase/uppercase character set
            _ when character >= 0x41 && character <= 0x5A => ((char)(character - 0x41 + 97)).ToString(),
            _ when character >= 0xC1 && character <= 0xDA => ((char)(character - 0xC1 + 65)).ToString(),
            _ => ((char)character).ToString()
        };

        var ram = new byte[0x10000];

        unsafe void SetupTest(string fileName, out Aemula.Emulation.Chips.Mos6502.Mos6502Chip cpu)
        {
            cpu = new Mos6502Chip(Mos6502Options.Default);

            // Note that we don't clear the RAM.
            // The tests always (?) write to RAM before reading.

            // Load test data.
            // First two bytes contain starting address.
            var path = Path.Combine(AssetsPath, "C64TestSuite", "bin", fileName);
            var testData = File.ReadAllBytes(path);
            var startAddress = testData[0] | testData[1] << 8;
            for (var i = 2; i < testData.Length; i++)
            {
                ram[startAddress + i - 2] = testData[i];
            }

            // Initialize some memory locations.
            ram[0x0002] = 0x00;
            ram[0xA002] = 0x00;
            ram[0xA003] = 0x80;
            ram[0xFFFE] = 0x48;
            ram[0xFFFF] = 0xFF;
            ram[0x01FE] = 0xFF;
            ram[0x01FF] = 0x7F;

            // Install KERNAL "IRQ handler".
            Span<byte> irqRoutine = stackalloc byte[]
            {
                0x48,             // PHA
                0x8A,             // TXA
                0x48,             // PHA
                0x98,             // TYA
                0x48,             // PHA
                0xBA,             // TSX
                0xBD,
                0x04,
                0x01, // LDA $0104,X
                0x29,
                0x10,       // AND #$10
                0xF0,
                0x03,       // BEQ $FF58
                0x6C,
                0x16,
                0x03, // JMP ($0316)
                0x6C,
                0x14,
                0x03, // JMP ($0314)
            };
            for (var i = 0; i < irqRoutine.Length; i++)
            {
                ram[0xFF48 + i] = irqRoutine[i];
            }

            // Stub CHROUT routine.
            ram[0xFFD2] = 0x60; // RTS

            // Stub load routine.
            ram[0xE16F] = 0xEA; // NOP

            // Stub GETIN routine.
            ram[0xFFE4] = 0xA9; // LDA #3
            ram[0xFFE5] = 0x03;
            ram[0xFFE6] = 0x60; // RTS

            // Initialize registers.
            cpu.SP = 0xFD;
            cpu.P.I = true;

            // Initialize RESET vector.
            ram[0xFFFC] = 0x01;
            ram[0xFFFD] = 0x08;
        }

        var log = new StringBuilder();
        var testFileName = " start";

        while (true)
        {
            SetupTest(testFileName, out var cpu);

            ref var pins = ref cpu.Pins;

            var continueTest = true;
            while (continueTest)
            {
                cpu.Tick();

                var address = pins.Address;

                if (pins.RW)
                {
                    switch (address)
                    {
                        case 0xFFD2: // Print character
                            if (cpu.A == 13)
                            {
                                Debug.WriteLine(log.ToString());
                                log.Clear();
                            }
                            else
                            {
                                log.Append(PetsciiToAscii(cpu.A));
                            }
                            ram[0x030C] = 0x00;
                            break;

                        case 0xE16F: // Load
                            var fileNameAddress = ram[0xBB] | ram[0xBC] << 8;
                            var fileNameLength = ram[0xB7];
                            testFileName = string.Empty;
                            for (var i = 0; i < fileNameLength; i++)
                            {
                                testFileName += PetsciiToAscii(ram[fileNameAddress + i]);
                            }
                            if (testFileName == "trap17")
                            {
                                // All tests passed. Everything from trap17 onwards is C64-specific.
                                return;
                            }
                            continueTest = false; // Break to outer loop, and load next test.
                            break;

                        case 0x8000: // Exit
                        case 0xA474:
                            Assert.Fail(log.ToString());
                            break;
                    }

                    pins.Data = ram[address];
                }
                else
                {
                    ram[address] = pins.Data;
                }
            }
        }
    }
}
