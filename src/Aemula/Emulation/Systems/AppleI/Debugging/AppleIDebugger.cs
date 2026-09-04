using System.Collections.Generic;
using Aemula.Debugging;
using Aemula.Emulation.Chips.Mos6502.Debugging;
using Aemula.UI;

namespace Aemula.Emulation.Systems.AppleI.Debugging;

// Phase 0 scaffolding (see docs/apple-i-plan.md): just enough to inspect the
// CPU and raw memory. No ScreenDisplayWindow/LogicAnalyzerWindow/
// TelevisionWindow yet - those need the video timing and composite video
// work from later phases.
public sealed class AppleIDebugger : Debugger
{
    private readonly AppleISystem _appleI;
    private readonly Mos6502Debugger _mos6502Debugger;

    public AppleIDebugger(AppleISystem appleI)
        : base(appleI, CreateMemoryCallbacks(appleI))
    {
        _appleI = appleI;

        _mos6502Debugger = new Mos6502Debugger(appleI.Cpu);
        _mos6502Debugger.RegisterStepModes(this);
    }

    private static DebuggerMemoryCallbacks CreateMemoryCallbacks(AppleISystem appleI)
    {
        return new DebuggerMemoryCallbacks(appleI.ReadByteDebug, appleI.WriteByteDebug);
    }

    protected override Disassembler CreateDisassembler()
    {
        return new Mos6502Disassembler(MemoryCallbacks, new Dictionary<ushort, string>());
    }

    protected override void TickSystem()
    {
        base.TickSystem();

        if (_appleI.Cpu.Sync && _appleI.Cpu.FinishedReset)
        {
            OnAddressExecuting(_appleI.Cpu.Address);
        }
    }

    public override void CreateDebuggerWindows(List<DebuggerWindow> result)
    {
        base.CreateDebuggerWindows(result);

        _appleI.Cpu.CreateDebuggerWindows(result);

        result.Add(new BreakpointsWindow(this));
        result.Add(new MemoryEditor(1, address => _appleI.ReadByteDebug((ushort)address), (address, data) => _appleI.WriteByteDebug((ushort)address, data)));
    }
}
