using System.Collections.Generic;
using Aemula.Debugging;
using Aemula.Emulation.Chips.Mos6502.Debugging;

namespace Aemula.Emulation.Systems.BbcMicro.Debugging;

public sealed class BbcMicroDebugger : Debugger
{
    private readonly BbcMicroSystem _system;

    public BbcMicroDebugger(BbcMicroSystem system)
        : base(system, new DebuggerMemoryCallbacks(system.ReadByteDebug, system.WriteByteDebug))
    {
        _system = system;

        AttachCpuDebugger(new Mos6502Debugger(system.Cpu));
    }

    protected override Disassembler CreateDisassembler()
    {
        return new Mos6502Disassembler(
            MemoryCallbacks,
            new Dictionary<ushort, string>(),
            registerCallbacks: new Mos6502RegisterCallbacks(() => _system.Cpu.X, () => _system.Cpu.Y));
    }
}
