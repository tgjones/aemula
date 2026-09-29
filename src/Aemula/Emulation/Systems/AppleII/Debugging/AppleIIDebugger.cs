using System.Collections.Generic;
using Aemula.Debugging;
using Aemula.Emulation.Chips.Mos6502.Debugging;

namespace Aemula.Emulation.Systems.AppleII.Debugging;

public sealed class AppleIIDebugger : Debugger
{
    private readonly AppleIISystem _appleII;

    public AppleIIDebugger(AppleIISystem appleII)
        : base(appleII, CreateMemoryCallbacks(appleII))
    {
        _appleII = appleII;

        AttachCpuDebugger(new Mos6502Debugger(appleII.Cpu));
    }

    private static DebuggerMemoryCallbacks CreateMemoryCallbacks(AppleIISystem appleII)
    {
        return new DebuggerMemoryCallbacks(appleII.ReadByteDebug, appleII.WriteByteDebug);
    }

    protected override Disassembler CreateDisassembler()
    {
        return new Mos6502Disassembler(
            MemoryCallbacks,
            new Dictionary<ushort, string>(),
            registerCallbacks: new Mos6502RegisterCallbacks(() => _appleII.Cpu.X, () => _appleII.Cpu.Y));
    }
}
