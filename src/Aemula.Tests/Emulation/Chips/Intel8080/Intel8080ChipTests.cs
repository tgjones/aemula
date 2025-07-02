using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Aemula.Emulation.Chips.Intel8080;

namespace Aemula.Tests.Emulation.Chips.Intel8080;

public class Intel8080ChipTests
{
    [Test]
    [Arguments("TST8080.COM", 4924ul)]
    [Arguments("CPUTEST.COM", 255653383ul)]
    [Arguments("8080PRE.COM", 7817ul)]
    [Arguments("8080EXM.COM", 23803381171ul)]
    public async Task Test8080(string fileName, ulong expectedCycleCount)
    {
        var programBytes = File.ReadAllBytes($"Emulation/Chips/Intel8080/Assets/{fileName}");

        var ram = new byte[0x10000];

        Array.Copy(programBytes, 0, ram, 0x100, programBytes.Length);

        var cpu = new Intel8080Chip();
        cpu.PC.Value = 0x100;

        // Patch C/PM "WBOOT" with "OUT 0, A".
        // This is a signal to stop the test.
        ram[0x0000] = 0xD3;
        ram[0x0001] = 0x00;

        // Patch C/PM "BDOS" with "OUT 1, A" followed by "RET".
        // This is a signal to output some characters.
        ram[0x0005] = 0xD3;
        ram[0x0006] = 0x01;
        ram[0x0007] = 0xC9;

        var cycleCount = 0ul;

        var output = new StringBuilder();

        byte lastStatusWord = 0;

        while (true)
        {
            cpu.Cycle();

            if (cpu.Pins.Sync)
            {
                lastStatusWord = cpu.Pins.Data;
            }

            cycleCount++;

            if (cycleCount > expectedCycleCount)
            {
                Assert.Fail("Exceeded expected cycle count");
            }

            if (cpu.Pins.DBIn)
            {
                switch (lastStatusWord)
                {
                    case Intel8080Chip.StatusWordFetch:
                    case Intel8080Chip.StatusWordMemoryRead:
                    case Intel8080Chip.StatusWordStackRead:
                        cpu.Pins.Data = ram[cpu.Pins.Address];
                        break;
                }
            }

            if (!cpu.Pins.Wr)
            {
                switch (lastStatusWord)
                {
                    case Intel8080Chip.StatusWordMemoryWrite:
                    case Intel8080Chip.StatusWordStackWrite:
                        ram[cpu.Pins.Address] = cpu.Pins.Data;
                        break;

                    case Intel8080Chip.StatusWordOutputWrite:
                        switch (cpu.Pins.Address & 0xFF)
                        {
                            case 0:
                                var outputText = output.ToString();
                                if (outputText.Contains("FAILED"))
                                {
                                    Assert.Fail(outputText);
                                }
                                else
                                {
                                    await Assert.That(cycleCount).IsEqualTo(expectedCycleCount).Because(outputText);
                                    Context.Current.OutputWriter.Write(outputText);

                                    // Successful.
                                    return;
                                }
                                break;

                            case 1:
                                switch (cpu.BC.C)
                                {
                                    // BDOS function 2 - console output.
                                    // Sends the character E to the screen.
                                    case 0x02:
                                        output.Append((char)cpu.DE.E);
                                        break;

                                    // BDOS function 9 - output string.
                                    // Displays a string of ASCII characters, terminated with the $ character.
                                    // DE contains the address of the string.
                                    case 0x09:
                                        var characterAddress = cpu.DE.Value;
                                        do
                                        {
                                            output.Append((char)ram[characterAddress++]);
                                        } while (ram[characterAddress] != '$');
                                        break;

                                    default:
                                        throw new InvalidOperationException();
                                }
                                break;

                            default:
                                throw new InvalidOperationException();
                        }
                        break;
                }
            }
        }
    }
}
