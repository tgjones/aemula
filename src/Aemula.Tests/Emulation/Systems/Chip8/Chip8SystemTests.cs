using System.IO;

namespace Aemula.Tests.Emulation.Systems.Chip8;

public class Chip8SystemTests
{
    private static readonly string AssetsPath = Path.Combine("Emulation", "Systems", "Chip8", "Assets");

    [Test]
    public void TestBCTest()
    {
        var system = new Aemula.Emulation.Systems.Chip8.Chip8System();
        system.LoadProgram(Path.Combine(AssetsPath, "bc_test.ch8"));

        var maxCycles = 1000000;
        var cycles = 0;
        while (cycles < maxCycles)
        {
            var lastPC = system.PC;

            system.Tick();

            if (lastPC == 0x030E && system.PC == 0x030E)
            {
                // Successful
                return;
            }

            cycles++;
        }

        Assert.Fail("Shouldn't be here");
    }

    [Test]
    public void TestChip8TestRom()
    {
        var system = new Aemula.Emulation.Systems.Chip8.Chip8System();
        system.LoadProgram(Path.Combine(AssetsPath, "test_opcode.ch8"));

        var maxCycles = 1000000;
        var cycles = 0;
        while (cycles < maxCycles)
        {
            var lastPC = system.PC;

            system.Tick();

            if (lastPC == 0x03DC && system.PC == 0x03DC)
            {
                // Successful
                return;
            }

            cycles++;
        }

        Assert.Fail("Shouldn't be here");
    }
}
