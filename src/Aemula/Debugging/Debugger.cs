using System;
using System.Collections.Generic;
using Aemula.UI;

namespace Aemula.Debugging;

public abstract class Debugger
{
    public readonly EmulatedSystem System;

    public readonly DebuggerMemoryCallbacks MemoryCallbacks;

    public readonly List<DebuggerStepMode> StepModes = [];
    public int ActiveStepModeIndex;

    public readonly BreakpointManager Breakpoints;

    public readonly Disassembler Disassembler;

    public ushort LastPC { get; private set; }

    public bool Stopped;

    /// <summary>
    /// Raised once per tick actually executed (free-run or single-step alike,
    /// since both funnel through <see cref="RunForDuration"/>). Used by
    /// <see cref="UI.LogicAnalyzer.LogicAnalyzerWindow"/> to sample channels.
    /// </summary>
    public event Action? Ticked;

    public Debugger(EmulatedSystem system, in DebuggerMemoryCallbacks memoryCallbacks)
    {
        System = system;

        MemoryCallbacks = memoryCallbacks;

        Breakpoints = new BreakpointManager(memoryCallbacks);

        Disassembler = CreateDisassembler();

        // Whenever the media changes (a cartridge swapped in), the code map is
        // different - re-walk it. Order-independent: subscribing after the
        // media is already in just means the first walk waits for the next
        // change, and lazy per-instruction disassembly covers the gap.
        System.MediaChanged += (sender, e) => Disassembler.Reset();

        ActiveStepModeIndex = 1;

        Stopped = true;
    }

    protected abstract Disassembler CreateDisassembler();

    public void RunForDuration(TimeSpan duration)
    {
        var clocks = duration.ToSystemTicks(System.CyclesPerSecond);

        for (var i = 0; i < clocks && !Stopped; i++)
        {
            var previousPC = LastPC;

            TickSystem();

            Ticked?.Invoke();

            if (ActiveStepModeIndex > -1)
            {
                var stepMode = StepModes[ActiveStepModeIndex];
                if (stepMode.ShouldStop())
                {
                    ActiveStepModeIndex = -1;
                    Stopped = true;
                }
            }

            if (previousPC != LastPC)
            {
                if (Breakpoints.ShouldBreak(LastPC))
                {
                    Stopped = true;
                }
            }
        }
    }

    protected virtual void TickSystem()
    {
        System.Tick();
    }

    protected void OnAddressExecuting(ushort address)
    {
        LastPC = address;

        Disassembler.OnAddressExecuting(address);
    }

    public virtual void CreateDebuggerWindows(List<DebuggerWindow> result)
    {
        result.Add(new DisassemblyWindow(this));
    }
}
