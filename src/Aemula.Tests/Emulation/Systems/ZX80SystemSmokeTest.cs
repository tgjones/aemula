using System.Threading.Tasks;
using Aemula.Emulation.Chips.Z80;
using Aemula.Emulation.Systems.ZX80;

namespace Aemula.Tests.Emulation.Systems;

public class ZX80SystemSmokeTest
{
    [Test]
    public async Task RunsResetVectorAndInitializesStackPointer()
    {
        var system = new ZX80System();

        ushort maxPc = 0;
        ushort minSp = 0xFFFF;

        // 50M ticks (12.5M T-states at 4 ticks/T-state) is enough for the ROM
        // to run its init code - including the RAM-clear loop at $0261,
        // which alone takes a large fraction of this - and reach the point
        // where it starts trying to generate a video signal, which isn't
        // wired up yet.
        for (var i = 0; i < 50_000_000; i++)
        {
            system.Tick();

            if (system.Cpu.CurrentMachineCycle == Z80Chip.MachineCycleType.OpcodeFetch &&
                system.Cpu.CurrentState == Z80Chip.TState.T1 &&
                system.Cpu.Address > maxPc)
            {
                maxPc = system.Cpu.Address;
            }

            if (system.Cpu.SP.Value < minSp)
            {
                minSp = system.Cpu.SP.Value;
            }
        }

        // If either of these never moved, the CPU never got past the reset
        // vector - the ROM's own init code runs well past address 0x10 and
        // sets SP into RAM (0x4000-0x43FF) before doing anything else.
        await Assert.That(maxPc).IsGreaterThan((ushort)0x10);
        await Assert.That(minSp).IsLessThan((ushort)0xFFFF);
        await Assert.That(minSp).IsGreaterThanOrEqualTo((ushort)0x4000);
        await Assert.That(minSp).IsLessThanOrEqualTo((ushort)0x43FF);
    }
}
