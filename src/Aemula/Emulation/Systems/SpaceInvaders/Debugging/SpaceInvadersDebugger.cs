using System.Collections.Generic;
using Aemula.Chips.Intel8080;
using Aemula.Chips.Intel8080.Debugging;
using Aemula.Core.Debugging;
using Aemula.Core.UI;

namespace Aemula.Systems.SpaceInvaders.Debugging;

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

        if (_system.Cpu.Pins.Sync && _system.Cpu.Pins.Data == Intel8080.StatusWordFetch)
        {
            OnAddressExecuting(_system.Cpu.Pins.Address);
        }
    }

    public override IEnumerable<DebuggerWindow> CreateDebuggerWindows()
    {
        foreach (var debuggerWindow in base.CreateDebuggerWindows())
        {
            yield return debuggerWindow;
        }

        foreach (var debuggerWindow in _system.Cpu.CreateDebuggerWindows())
        {
            yield return debuggerWindow;
        }

        yield return new ScreenDisplayWindow(_system.Display, 90);
    }
}
