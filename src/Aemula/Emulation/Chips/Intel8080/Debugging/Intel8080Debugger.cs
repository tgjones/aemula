using Aemula.Debugging;

namespace Aemula.Emulation.Chips.Intel8080.Debugging;

public sealed class Intel8080Debugger : CpuDebugger
{
    private readonly Intel8080Chip _cpu;

    private ushort _startPC;
    private int _startState;
    private int _lastPolledState;
    private bool _lastPolledFetching;

    public Intel8080Debugger(Intel8080Chip cpu)
    {
        _cpu = cpu;
    }

    /// <remarks>
    /// The 8080 has no fixed-length cycle, so a "cycle" here is a state
    /// change: one clock cycle of the CPU's own clock.
    /// </remarks>
    public override bool PollCycleAdvanced()
    {
        var state = _cpu.CombinedMachineCycleTypeAndState;
        var advanced = state != _lastPolledState;
        _lastPolledState = state;
        return advanced;
    }

    public override bool PollFetch(out ushort address)
    {
        var fetching = _cpu.Sync && _cpu.Data == Intel8080Chip.StatusWordFetch;
        var started = fetching && !_lastPolledFetching;
        _lastPolledFetching = fetching;

        address = _cpu.Address;
        return started;
    }

    public override void RegisterStepModes(Debugger debugger)
    {
        debugger.StepModes.Add(
            new DebuggerStepMode(
                "Step Instruction",
                () => _cpu.Sync && _cpu.Data == Intel8080Chip.StatusWordFetch && _cpu.Address != _startPC,
                () => _startPC = _cpu.Address));

        debugger.StepModes.Add(
            new DebuggerStepMode(
                "Step CPU Cycle",
                () => _cpu.CombinedMachineCycleTypeAndState != _startState,
                () => _startState = _cpu.CombinedMachineCycleTypeAndState));
    }
}
