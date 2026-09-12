using Aemula.Debugging;
using Aemula.Emulation.Chips.Z80.Debugging;

namespace Aemula.Emulation.Systems.ZX80.Debugging;

public sealed class ZX80Debugger : Debugger
{
    public ZX80Debugger(ZX80System system)
        : base(system, new DebuggerMemoryCallbacks(system.ReadByteDebug, system.WriteByteDebug))
    {
    }

    protected override Disassembler CreateDisassembler()
    {
        return new Z80Disassembler(MemoryCallbacks);
    }
}
