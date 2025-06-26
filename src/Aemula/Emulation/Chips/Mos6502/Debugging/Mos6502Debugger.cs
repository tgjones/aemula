using Aemula.Core.Debugging;

namespace Aemula.Chips.Mos6502.Debugging;

public sealed class Mos6502Debugger
{
    public readonly Mos6502 Cpu;

    // TODO: StepOverPC for "step over" step mode

    private ushort _startPC;
    private byte _startTR;

    public Mos6502Debugger(Mos6502 cpu)
    {
        Cpu = cpu;
    }

    public void RegisterStepModes(Debugger debugger)
    {
        debugger.StepModes.Add(new DebuggerStepMode("Step Instruction", () => Cpu.Pins.Sync && Cpu.Pins.Address != _startPC, () => _startPC = Cpu.Pins.Address));
        debugger.StepModes.Add(new DebuggerStepMode("Step CPU Cycle", () => Cpu.TR != _startTR, () => _startTR = Cpu.TR));
    }
}
