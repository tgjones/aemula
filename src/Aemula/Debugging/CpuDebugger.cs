namespace Aemula.Debugging;

/// <summary>
/// Chip-specific half of a <see cref="Debugger"/>: knows how to read one CPU's
/// pins and internal state to report cycle boundaries and instruction
/// fetches, so systems containing that CPU don't need to. Both polls are
/// stateful (they edge-detect against the previous call), so
/// <see cref="Debugger"/> calls each exactly once per system tick.
/// </summary>
public abstract class CpuDebugger : ChipDebugger
{
    /// <summary>
    /// True on each tick where the CPU has moved on to a new CPU cycle since
    /// the last call. CPU cycles rather than system ticks, since a system's
    /// master clock runs several ticks per CPU cycle.
    /// </summary>
    public abstract bool PollCycleAdvanced();

    /// <summary>
    /// True on the first tick of each instruction fetch, with the address
    /// being fetched from. Fetch signals typically stay asserted for a whole
    /// CPU cycle, i.e. several system ticks, so implementations report only
    /// the leading edge.
    /// </summary>
    public abstract bool PollFetch(out ushort address);

    /// <summary>
    /// True on the tick where the CPU starts writing a byte to memory, with
    /// the address being written to. Reports each write cycle once, even when
    /// consecutive cycles write (e.g. pushes), and ignores I/O writes.
    /// </summary>
    public virtual bool PollWrite(out ushort address)
    {
        address = 0;
        return false;
    }

    public abstract void RegisterStepModes(Debugger debugger);
}
