using Aemula.Debugging;
using Aemula.Debugging.LogicAnalyzer;

namespace Aemula.Emulation.Chips.Intel8080.Debugging;

public sealed class Intel8080Debugger : CpuDebugger
{
    private readonly Intel8080Chip _cpu;

    private ushort _startPC;
    private int _startState;
    private int _lastPolledState;
    private bool _lastPolledFetching;
    private bool _lastPolledWriting;
    private bool _memoryWriteCycle;

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

    /// <remarks>
    /// /WR is also asserted for output cycles, so the status word latched
    /// during SYNC says whether this is a memory (or stack) write. /WR goes
    /// high again at the start of every machine cycle, so its falling edge
    /// marks each write.
    /// </remarks>
    public override bool PollWrite(out ushort address)
    {
        if (_cpu.Sync)
        {
            _memoryWriteCycle = _cpu.Data is Intel8080Chip.StatusWordMemoryWrite or Intel8080Chip.StatusWordStackWrite;
        }

        var writing = !_cpu.Wr && _memoryWriteCycle;
        var started = writing && !_lastPolledWriting;
        _lastPolledWriting = writing;

        address = _cpu.Address;
        return started;
    }

    public override ChannelGroup CreateChannelGroup()
    {
        return new ChannelGroup("Intel 8080",
        [
            Channel.Bus("Address", 16, () => _cpu.Address),
            Channel.Bus("Data", 8, () => _cpu.Data),
            Channel.Digital("SYNC", () => _cpu.Sync),
            Channel.Digital("DBIN", () => _cpu.DBIn),
            Channel.Digital("WR", () => _cpu.Wr),
            Channel.Digital("WAIT", () => _cpu.Wait),
            Channel.Digital("INTE", () => _cpu.IntE),
            Channel.Digital("HLDA", () => _cpu.HldA),
            Channel.Digital("RESET", () => _cpu.Reset),
            Channel.Digital("HOLD", () => _cpu.Hold),
            Channel.Digital("INT", () => _cpu.Int),
            Channel.Digital("READY", () => _cpu.Ready),
            Channel.Digital("PHI1", () => _cpu.Phi1),
            Channel.Digital("PHI2", () => _cpu.Phi2),
        ]);
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
