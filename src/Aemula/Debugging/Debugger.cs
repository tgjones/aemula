using System;
using System.Collections.Generic;
using Aemula.UI;

namespace Aemula.Debugging;

public abstract class Debugger
{
    public readonly EmulatedSystem System;

    public readonly DebuggerMemoryCallbacks MemoryCallbacks;

    public readonly List<DebuggerStepMode> StepModes = new List<DebuggerStepMode>();
    public int ActiveStepModeIndex;

    public readonly BreakpointManager Breakpoints;

    public readonly Disassembler Disassembler;

    public ushort LastPC { get; private set; }

    public bool Stopped;

    public Debugger(EmulatedSystem system, in DebuggerMemoryCallbacks memoryCallbacks)
    {
        System = system;

        MemoryCallbacks = memoryCallbacks;

        Breakpoints = new BreakpointManager(memoryCallbacks);

        Disassembler = CreateDisassembler();

        System.ProgramLoaded += (sender, e) => Disassembler.Reset();

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

    public virtual IEnumerable<DebuggerWindow> CreateDebuggerWindows()
    {
        yield return new DisassemblyWindow(this);
    }
}
