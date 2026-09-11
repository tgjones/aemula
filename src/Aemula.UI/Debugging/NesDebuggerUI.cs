using System.Collections.Generic;
using Aemula.Emulation.Systems.Nes;
using Aemula.Emulation.Systems.Nes.Debugging;
using Aemula.UI.Chips.Mos6502;
using Aemula.UI.Chips.Ricoh2C02;
using Aemula.UI.Systems.Nes;

namespace Aemula.UI.Debugging;

internal sealed class NesDebuggerUI(NesDebugger debugger) : DebuggerUI(debugger)
{
    private readonly NesSystem _nes = (NesSystem)debugger.System;

    public override void CreateDebuggerWindows(List<DebuggerWindow> result)
    {
        base.CreateDebuggerWindows(result);

        result.Add(new CpuStateWindow(_nes.Cpu.CpuCore));
        result.Add(new PpuStateWindow(_nes.Ppu));
        result.Add(new PaletteWindow(_nes.Ppu));

        result.Add(new BreakpointsWindow(debugger));
        result.Add(new MemoryEditor(1, address => _nes.ReadByteDebug((ushort)address), (address, data) => _nes.WriteByteDebug((ushort)address, data)));
        result.Add(new PatternTableWindow(_nes));
        result.Add(new TelevisionWindow(_nes.Television));
    }
}
