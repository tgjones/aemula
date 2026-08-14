using System.Threading.Tasks;
using Aemula.Emulation.Systems.AppleII;

namespace Aemula.Tests.Emulation.Systems.AppleII;

public class AppleIISystemTests
{
    [Test]
    public async Task RunsResetVectorFromRom()
    {
        var system = new AppleIISystem();
        system.LoadProgram("");

        // The Autostart ROM's reset vector, read straight from Apple2_Plus.rom.
        const ushort resetVector = 0xFA62;

        var maxCycles = 100_000;
        var cycles = 0;
        var reachedResetVector = false;

        while (cycles < maxCycles)
        {
            system.Tick();

            if (system.Cpu.PC == resetVector)
            {
                reachedResetVector = true;
                break;
            }

            cycles++;
        }

        await Assert.That(reachedResetVector).IsTrue();
    }
}
