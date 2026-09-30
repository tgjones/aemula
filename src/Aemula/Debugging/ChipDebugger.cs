using Aemula.Debugging.LogicAnalyzer;

namespace Aemula.Debugging;

/// <summary>
/// Chip-specific half of a <see cref="Debugger"/>: knows one chip's pins, so
/// systems containing that chip don't each need to list them. Chips with
/// nothing more to debug than their pins (TIA, RIOT) derive from this
/// directly; CPUs derive from <see cref="CpuDebugger"/>.
/// </summary>
public abstract class ChipDebugger
{
    /// <summary>
    /// The logic analyzer channels for this chip's pins.
    /// </summary>
    public abstract ChannelGroup CreateChannelGroup();
}
