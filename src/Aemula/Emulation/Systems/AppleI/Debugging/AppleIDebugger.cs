using System.Collections.Generic;
using Aemula.Debugging;
using Aemula.Emulation.Chips.Mos6502.Debugging;

namespace Aemula.Emulation.Systems.AppleI.Debugging;

// The CPU/memory views, plus the composite video picture through
// TelevisionWindow now that AppleISystem drives Television for real (see
// AppleISystem.CompositeVideo.cs). No ScreenDisplayWindow - unlike Apple II,
// there's no DisplayBuffer here (see AppleISystem.Video.cs's remarks on the
// read-side simplification); the composite picture is the only view. No
// LogicAnalyzerWindow yet either.
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
        return new Mos6502Disassembler(
            MemoryCallbacks,
            new Dictionary<ushort, string>(),
            registerCallbacks: new Mos6502RegisterCallbacks(() => _appleI.Cpu.X, () => _appleI.Cpu.Y));
    }

    protected override void TickSystem()
    {
        base.TickSystem();

        if (_appleI.Cpu.Sync && _appleI.Cpu.FinishedReset)
        {
            OnAddressExecuting(_appleI.Cpu.Address);
        }
    }
}
