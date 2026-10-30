using System.Collections.Generic;
using Aemula.Emulation.Systems.BbcMicro;
using Aemula.Emulation.Systems.BbcMicro.Debugging;
using Aemula.UI.Chips.Mos6502;

namespace Aemula.UI.Debugging;

internal sealed class BbcMicroDebuggerUI(BbcMicroDebugger debugger) : DebuggerUI(debugger)
{
    private readonly BbcMicroSystem _system = (BbcMicroSystem)debugger.System;

    public override void CreateDebuggerWindows(List<DebuggerWindow> result)
    {
        base.CreateDebuggerWindows(result);

        result.Add(new CpuStateWindow(_system.Cpu));

        result.Add(new BreakpointsWindow(debugger));
        result.Add(new MemoryEditor(1, address => _system.ReadByteDebug((ushort)address), (address, data) => _system.WriteByteDebug((ushort)address, data)));
        result.Add(new TelevisionWindow(_system.Television));
    }
}
