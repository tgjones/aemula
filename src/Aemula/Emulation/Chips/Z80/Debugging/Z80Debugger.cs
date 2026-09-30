using Aemula.Debugging;
using Aemula.Debugging.LogicAnalyzer;

namespace Aemula.Emulation.Chips.Z80.Debugging;

public sealed class Z80Debugger(Z80Chip cpu) : CpuDebugger
{
    private int _lastPolledCycleState = -1;
    private bool _lastPolledM1 = true;
    private bool _lastPolledWriting;
    private bool _leftStartBoundary;
    private int _startCycleKey;

    /// <remarks>
    /// One cycle is one T-state. The machine-cycle type is part of the key so
    /// a new machine cycle starting in the same T-state number as the last
    /// one still registers as a new cycle.
    /// </remarks>
    public override bool PollCycleAdvanced()
    {
        var cycleState = ((int)cpu.CurrentMachineCycle << 8) | (int)cpu.CurrentState;
        var advanced = cycleState != _lastPolledCycleState;
        _lastPolledCycleState = cycleState;
        return advanced;
    }

    /// <remarks>
    /// /M1 stays low for the whole opcode-fetch machine cycle; only its
    /// falling edge (T1, where the address bus is freshly latched to PC)
    /// marks the start of a new instruction fetch.
    /// </remarks>
    public override bool PollFetch(out ushort address)
    {
        var m1 = cpu.M1;
        var started = !m1 && _lastPolledM1;
        _lastPolledM1 = m1;

        address = cpu.Address;
        return started;
    }

    /// <remarks>
    /// A memory write is /WR low with /MREQ low (an I/O write has /IORQ low
    /// instead); /WR goes high between machine cycles, so its falling edge
    /// marks each write.
    /// </remarks>
    public override bool PollWrite(out ushort address)
    {
        var writing = !cpu.Wr && !cpu.MReq;
        var started = writing && !_lastPolledWriting;
        _lastPolledWriting = writing;

        address = cpu.Address;
        return started;
    }

    /// <remarks>
    /// An instruction step ends at the next instruction boundary, so prefixed
    /// instructions count as one step and a halted CPU steps one NOP per
    /// M1. A step requested while already on a boundary must first leave it.
    /// </remarks>
    public override ChannelGroup CreateChannelGroup()
    {
        // Active-low pins are shown as their raw level, like the
        // datasheet's overbar names.
        return new ChannelGroup("Z80",
        [
            Channel.Bus("Address", 16, () => cpu.Address),
            Channel.Bus("Data", 8, () => cpu.Data),
            Channel.Digital("M1", () => cpu.M1),
            Channel.Digital("MREQ", () => cpu.MReq),
            Channel.Digital("IORQ", () => cpu.IoRq),
            Channel.Digital("RD", () => cpu.Rd),
            Channel.Digital("WR", () => cpu.Wr),
            Channel.Digital("RFSH", () => cpu.Rfsh),
            Channel.Digital("HALT", () => cpu.Halt),
            Channel.Digital("WAIT", () => cpu.Wait),
            Channel.Digital("INT", () => cpu.Int),
            Channel.Digital("NMI", () => cpu.Nmi),
            Channel.Digital("BUSRQ", () => cpu.BusRq),
            Channel.Digital("BUSAK", () => cpu.BusAk),
            Channel.Digital("RESET", () => cpu.Reset),
            Channel.Digital("CLK", () => cpu.Clk),
        ]);
    }

    public override void RegisterStepModes(Debugger debugger)
    {
        debugger.StepModes.Add(
            new DebuggerStepMode(
                "Step Instruction",
                () =>
                {
                    if (!cpu.AtInstructionBoundary)
                    {
                        _leftStartBoundary = true;
                        return false;
                    }
                    return _leftStartBoundary;
                },
                () => _leftStartBoundary = !cpu.AtInstructionBoundary));

        debugger.StepModes.Add(
            new DebuggerStepMode(
                "Step CPU Cycle",
                () => cpu.CombinedCycleKey != _startCycleKey,
                () => _startCycleKey = cpu.CombinedCycleKey));
    }
}
