using System.Collections.Generic;
using Aemula.Debugging;
using Aemula.Emulation.Chips.Mos6502.Debugging;
using Aemula.UI;
using Aemula.UI.Oscilloscope;

namespace Aemula.Emulation.Systems.AppleII.Debugging;

public sealed class AppleIIDebugger : Debugger
{
    private readonly AppleIISystem _appleII;
    private readonly Mos6502Debugger _mos6502Debugger;

    public AppleIIDebugger(AppleIISystem appleII)
        : base(appleII, CreateMemoryCallbacks(appleII))
    {
        _appleII = appleII;

        _mos6502Debugger = new Mos6502Debugger(appleII.Cpu);
        _mos6502Debugger.RegisterStepModes(this);
    }

    private static DebuggerMemoryCallbacks CreateMemoryCallbacks(AppleIISystem appleII)
    {
        return new DebuggerMemoryCallbacks(appleII.ReadByteDebug, appleII.WriteByteDebug);
    }

    protected override Disassembler CreateDisassembler()
    {
        return new Mos6502Disassembler(MemoryCallbacks, new Dictionary<ushort, string>());
    }

    protected override void TickSystem()
    {
        base.TickSystem();

        if (_appleII.Cpu.Sync && _appleII.Cpu.FinishedReset)
        {
            OnAddressExecuting(_appleII.Cpu.Address);
        }
    }

    public override void CreateDebuggerWindows(List<DebuggerWindow> result)
    {
        base.CreateDebuggerWindows(result);

        _appleII.Cpu.CreateDebuggerWindows(result);

        result.Add(new BreakpointsWindow(this));
        result.Add(new MemoryEditor(1, address => _appleII.ReadByteDebug((ushort)address), (address, data) => _appleII.WriteByteDebug((ushort)address, data)));
        result.Add(new ScreenDisplayWindow(_appleII.Display));
        result.Add(new OscilloscopeWindow(this, _appleII.CreateScopeChannelGroup()));
    }
}
