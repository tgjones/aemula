using System.Collections.Generic;
using Aemula.Emulation.Systems.Atari2600;
using Aemula.Emulation.Systems.Atari2600.Debugging;
using Aemula.UI.Chips.Mos6502;
using Aemula.UI.Chips.Tia;
using Aemula.Debugging.LogicAnalyzer;

namespace Aemula.UI.Debugging;

internal sealed class Atari2600DebuggerUI(Atari2600Debugger debugger) : DebuggerUI(debugger)
{
    private readonly Atari2600System _system = (Atari2600System)debugger.System;

    public override void CreateDebuggerWindows(List<DebuggerWindow> result)
    {
        base.CreateDebuggerWindows(result);

        result.Add(new CpuStateWindow(_system.Cpu));
        result.Add(new TiaWindow(_system.Tia));

        result.Add(new BreakpointsWindow(debugger));
        result.Add(new TelevisionWindow(_system.Television));
    }

    // Composite Video (and every other channel alongside it) is recorded
    // at TIA's true 4x-oversampled rate, not the 1x tick rate Ticked
    // otherwise implies - see Atari2600System.CompositeVideoSampled's
    // remarks.
    protected override SampleClock? CreateSampleClock() => new SampleClock(
        _system.CyclesPerSecond * 4,
        h => _system.CompositeVideoSampled += h,
        h => _system.CompositeVideoSampled -= h);
}
