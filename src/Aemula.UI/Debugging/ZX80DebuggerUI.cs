using System.Collections.Generic;
using Aemula.Emulation.Systems.ZX80;
using Aemula.Emulation.Systems.ZX80.Debugging;
using Aemula.UI.Chips.Z80;

namespace Aemula.UI.Debugging;

internal sealed class ZX80DebuggerUI(ZX80Debugger debugger) : DebuggerUI(debugger)
{
    private readonly ZX80System _system = (ZX80System)debugger.System;

    public override void CreateDebuggerWindows(List<DebuggerWindow> result)
    {
        base.CreateDebuggerWindows(result);

        result.Add(new CpuStateWindow(_system.Cpu));

        result.Add(new BreakpointsWindow(debugger));
        result.Add(new MemoryEditor(1, address => _system.ReadByteDebug((ushort)address), (address, data) => _system.WriteByteDebug((ushort)address, data)));
        result.Add(new TelevisionWindow(_system.Television));
    }
}
