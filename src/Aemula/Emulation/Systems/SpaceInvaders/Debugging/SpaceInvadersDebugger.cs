using Aemula.Debugging;
using Aemula.Emulation.Chips.Intel8080.Debugging;

namespace Aemula.Emulation.Systems.SpaceInvaders.Debugging;

public sealed class SpaceInvadersDebugger : Debugger
{
    private readonly SpaceInvadersSystem _system;

    public SpaceInvadersDebugger(SpaceInvadersSystem system, in DebuggerMemoryCallbacks memoryCallbacks)
        : base(system, memoryCallbacks)
    {
        _system = system;

        AttachCpuDebugger(new Intel8080Debugger(_system.Cpu));

        ActiveStepModeIndex = 0;
    }

    protected override Disassembler CreateDisassembler()
    {
        return new Intel8080Disassembler(MemoryCallbacks);
    }
}
