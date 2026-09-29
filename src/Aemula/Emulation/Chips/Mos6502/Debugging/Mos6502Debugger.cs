using Aemula.Debugging;

namespace Aemula.Emulation.Chips.Mos6502.Debugging;

public sealed class Mos6502Debugger : CpuDebugger
{
    public readonly Mos6502Chip Cpu;

    // TODO: StepOverPC for "step over" step mode

    private ushort _startPC;
    private byte _startTR;
    private byte _lastPolledTR;
    private bool _lastPolledFetching;

    public Mos6502Debugger(Mos6502Chip cpu)
    {
        Cpu = cpu;
    }

    public override bool PollCycleAdvanced()
    {
        var tr = Cpu.TR;
        var advanced = tr != _lastPolledTR;
        _lastPolledTR = tr;
        return advanced;
    }

    public override bool PollFetch(out ushort address)
    {
        // Sync during the reset sequence isn't a real instruction fetch.
        var fetching = Cpu.Sync && Cpu.FinishedReset;
        var started = fetching && !_lastPolledFetching;
        _lastPolledFetching = fetching;

        address = Cpu.Address;
        return started;
    }

    public override void RegisterStepModes(Debugger debugger)
    {
        debugger.StepModes.Add(new DebuggerStepMode("Step Instruction", () => Cpu.Sync && Cpu.Address != _startPC, () => _startPC = Cpu.Address));
        debugger.StepModes.Add(new DebuggerStepMode("Step CPU Cycle", () => Cpu.TR != _startTR, () => _startTR = Cpu.TR));
    }
}
