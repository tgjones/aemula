using System.Collections.Generic;
using Aemula.Emulation.Systems.SpaceInvaders;
using Aemula.Emulation.Systems.SpaceInvaders.Debugging;
using Aemula.UI.Chips.Intel8080;

namespace Aemula.UI.Debugging;

internal sealed class SpaceInvadersDebuggerUI(SpaceInvadersDebugger debugger) : DebuggerUI(debugger)
{
    private readonly SpaceInvadersSystem _system = (SpaceInvadersSystem)debugger.System;

    public override void CreateDebuggerWindows(List<DebuggerWindow> result)
    {
        base.CreateDebuggerWindows(result);

        result.Add(new CpuStateWindow(_system.Cpu));

        result.Add(new TelevisionWindow(_system.Television));
    }
}
