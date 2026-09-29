using Aemula.Debugging;

namespace Aemula.Emulation.Chips.Z80.Debugging;

public sealed class Z80Debugger(Z80Chip cpu) : CpuDebugger
{
    private int _lastPolledCycleState = -1;
    private bool _lastPolledM1 = true;
    private bool _lastPolledWriting;

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

    // No step modes for the Z80 yet.
    public override void RegisterStepModes(Debugger debugger)
    {
    }
}
