using Aemula.Debugging;

namespace Aemula.Emulation.Systems.Chip8.Debugging;

internal sealed class Chip8Debugger : Debugger
{
    public Chip8Debugger(Chip8System system, DebuggerMemoryCallbacks memoryCallbacks)
        : base(system, memoryCallbacks)
    {
    }

    protected override Disassembler CreateDisassembler()
    {
        return new Chip8Disassembler(MemoryCallbacks);
    }
}
