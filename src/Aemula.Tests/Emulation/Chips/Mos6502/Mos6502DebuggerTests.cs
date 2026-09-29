using System.Collections.Generic;
using System.Threading.Tasks;
using Aemula.Emulation.Chips.Mos6502;
using Aemula.Emulation.Chips.Mos6502.Debugging;

namespace Aemula.Tests.Emulation.Chips.Mos6502;

public class Mos6502DebuggerTests
{
    [Test]
    public async Task PollWriteReportsEachWriteCycleOnce()
    {
        var memory = new byte[0x10000];

        // LDA #$01 / PHA / PHA / STA $0200 - back-to-back pushes are
        // consecutive write cycles with R/W low throughout.
        byte[] program = [0xA9, 0x01, 0x48, 0x48, 0x8D, 0x00, 0x02, 0x4C, 0x07, 0xF0];
        program.CopyTo(memory, 0xF000);
        memory[0xFFFC] = 0x00;
        memory[0xFFFD] = 0xF0;

        var cpu = new Mos6502Chip(Mos6502Options.Default);
        var debugger = new Mos6502Debugger(cpu);

        cpu.Res = false;
        cpu.Phi0 = true;
        cpu.Irq = true;
        cpu.Nmi = true;
        for (var i = 0; i < 8; i++)
        {
            cpu.Phi0 = false;
            cpu.Phi0 = true;
        }
        cpu.Res = true;

        var writes = new List<ushort>();

        for (var i = 0; i < 60; i++)
        {
            cpu.Phi0 = false;
            debugger.PollCycleAdvanced();
            if (debugger.PollWrite(out var lowAddress))
            {
                writes.Add(lowAddress);
            }

            cpu.Phi0 = true;
            if (cpu.RW)
            {
                cpu.Data = memory[cpu.Address];
            }
            else
            {
                memory[cpu.Address] = cpu.Data;
            }

            if (debugger.PollWrite(out var highAddress))
            {
                writes.Add(highAddress);
            }
        }

        // SP after reset isn't defined by the hardware, so only check the
        // pushes land on consecutive stack addresses.
        await Assert.That(writes.Count).IsEqualTo(3);
        await Assert.That(writes[0] >> 8).IsEqualTo(0x01);
        await Assert.That(writes[1]).IsEqualTo((ushort)(writes[0] - 1));
        await Assert.That(writes[2]).IsEqualTo((ushort)0x0200);
    }
}
