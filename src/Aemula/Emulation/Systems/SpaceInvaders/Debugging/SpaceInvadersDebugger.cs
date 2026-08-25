using System.Collections.Generic;
using Aemula.Debugging;
using Aemula.Emulation.Chips.Intel8080;
using Aemula.Emulation.Chips.Intel8080.Debugging;
using Aemula.UI;
using Aemula.UI.LogicAnalyzer;

namespace Aemula.Emulation.Systems.SpaceInvaders.Debugging;

public sealed class SpaceInvadersDebugger : Debugger
{
    private readonly SpaceInvadersSystem _system;
    private readonly Intel8080Debugger _intel8080Debugger;

    public SpaceInvadersDebugger(SpaceInvadersSystem system, in DebuggerMemoryCallbacks memoryCallbacks)
        : base(system, memoryCallbacks)
    {
        _system = system;

        _intel8080Debugger = new Intel8080Debugger(_system.Cpu);
        _intel8080Debugger.RegisterStepModes(this);

        ActiveStepModeIndex = 0;
    }

    protected override Disassembler CreateDisassembler()
    {
        return new Intel8080Disassembler(MemoryCallbacks);
    }

    protected override void TickSystem()
    {
        base.TickSystem();

        if (_system.Cpu.Sync && _system.Cpu.Data == Intel8080Chip.StatusWordFetch)
        {
            OnAddressExecuting(_system.Cpu.Address);
        }
    }

    public override void CreateDebuggerWindows(List<DebuggerWindow> result)
    {
        base.CreateDebuggerWindows(result);

        _system.Cpu.CreateDebuggerWindows(result);

        result.Add(new ScreenDisplayWindow(_system.Display, 90));
        result.Add(new LogicAnalyzerWindow(this, _system.CreateChannelNodes()));
    }
}
