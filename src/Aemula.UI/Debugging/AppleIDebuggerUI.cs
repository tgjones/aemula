using System.Collections.Generic;
using Aemula.Emulation.Systems.AppleI;
using Aemula.Emulation.Systems.AppleI.Debugging;
using Aemula.UI.Chips.Mos6502;

namespace Aemula.UI.Debugging;

internal sealed class AppleIDebuggerUI(AppleIDebugger debugger) : DebuggerUI(debugger)
{
    private readonly AppleISystem _appleI = (AppleISystem)debugger.System;

    public override void CreateDebuggerWindows(List<DebuggerWindow> result)
    {
        base.CreateDebuggerWindows(result);

        result.Add(new CpuStateWindow(_appleI.Cpu));

        result.Add(new BreakpointsWindow(debugger));
        result.Add(new MemoryEditor(1, address => _appleI.ReadByteDebug((ushort)address), (address, data) => _appleI.WriteByteDebug((ushort)address, data)));
        result.Add(new TelevisionWindow(_appleI.Television));
    }
}
