using System;
using System.IO;
using System.Text;
using NUnit.Framework;

namespace Aemula.Chips.Intel8080.Tests;

public class Intel8080Tests
{
    [TestCase("TST8080.COM", 4924ul)]
    [TestCase("CPUTEST.COM", 255653383ul)]
    [TestCase("8080PRE.COM", 7817ul)]
    [TestCase("8080EXM.COM", 23803381171ul)]
    public void Test8080(string fileName, ulong expectedCycleCount)
    {
        var programBytes = File.ReadAllBytes($"Emulation/Chips/Intel8080/Assets/{fileName}");

        var ram = new byte[0x10000];

        Array.Copy(programBytes, 0, ram, 0x100, programBytes.Length);

        var cpu = new Intel8080();
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
                    case Intel8080.StatusWordFetch:
                    case Intel8080.StatusWordMemoryRead:
                    case Intel8080.StatusWordStackRead:
                        cpu.Pins.Data = ram[cpu.Pins.Address];
                        break;
                }
            }

            if (!cpu.Pins.Wr)
            {
                switch (lastStatusWord)
                {
                    case Intel8080.StatusWordMemoryWrite:
                    case Intel8080.StatusWordStackWrite:
                        ram[cpu.Pins.Address] = cpu.Pins.Data;
                        break;

                    case Intel8080.StatusWordOutputWrite:
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
                                    Assert.AreEqual(expectedCycleCount, cycleCount, outputText);
                                    Assert.Pass(outputText);
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
