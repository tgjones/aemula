using System.Collections.Generic;
using Aemula.Emulation.Systems.AppleII;
using Aemula.Emulation.Systems.AppleII.Debugging;
using Aemula.UI.Chips.Mos6502;
using Aemula.UI.LogicAnalyzer;

namespace Aemula.UI.Debugging;

internal sealed class AppleIIDebuggerUI(AppleIIDebugger debugger) : DebuggerUI(debugger)
{
    private readonly AppleIISystem _appleII = (AppleIISystem)debugger.System;

    public override void CreateDebuggerWindows(List<DebuggerWindow> result)
    {
        base.CreateDebuggerWindows(result);

        result.Add(new CpuStateWindow(_appleII.Cpu));

        result.Add(new BreakpointsWindow(debugger));
        result.Add(new MemoryEditor(1, address => _appleII.ReadByteDebug((ushort)address), (address, data) => _appleII.WriteByteDebug((ushort)address, data)));
        result.Add(new LogicAnalyzerWindow(debugger, _appleII.CreateChannelNodes()));
        result.Add(new TelevisionWindow(_appleII.Television));
    }
}
