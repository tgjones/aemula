using System;
using System.Collections.Generic;

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
    /// Ticks actually executed since this debugger was created (free-run or
    /// single-step alike, since both funnel through <see cref="RunForDuration"/>).
    /// Used to compute <see cref="LastExecutionCycles"/> - a running counter
    /// rather than a per-instruction stopwatch, since it's cheap to bump once
    /// per tick and lets any two fetches' cycle gap be measured by subtraction.
    /// </summary>
    public long TotalTicks { get; private set; }

    /// <summary>
    /// How many ticks the instruction at each address actually took the last
    /// time it executed - the gap between that fetch and the next one,
    /// measured rather than looked up in a static per-opcode table, so it's
    /// correct for branches, page-crossing penalties, and interrupts for
    /// free. Zero means "hasn't completed an execution yet".
    /// </summary>
    public readonly int[] LastExecutionCycles = new int[0x10000];

    private long _previousExecutionTick;
    private ushort _previousExecutionAddress;
    private bool _hasPreviousExecution;

    // One-shot "run to cursor" target (see RunToAddress) - unlike a regular
    // breakpoint, hitting it clears it rather than leaving a permanent entry
    // in BreakpointManager behind.
    private ushort? _runToAddress;

    /// <summary>
    /// Raised once per tick actually executed (free-run or single-step alike,
    /// since both funnel through <see cref="RunForDuration"/>). Used by
    /// Aemula.UI.LogicAnalyzer.LogicAnalyzerWindow to sample channels.
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
        System.MediaChanged += (sender, e) =>
        {
            Disassembler.Reset();
            Array.Clear(LastExecutionCycles);
            _hasPreviousExecution = false;
            _runToAddress = null;
        };

        ActiveStepModeIndex = 1;

        Stopped = true;
    }

    protected abstract Disassembler CreateDisassembler();

    /// <summary>
    /// Runs until PC reaches <paramref name="address"/>, then stops - a
    /// one-shot target rather than a permanent breakpoint, cleared the
    /// moment it's hit (see RunForDuration). Doesn't itself resume a stopped
    /// debugger; callers set <see cref="Stopped"/> = false alongside this,
    /// the same way the "Continue" action does.
    /// </summary>
    public void RunToAddress(ushort address)
    {
        _runToAddress = address;
    }

    public void RunForDuration(TimeSpan duration)
    {
        var clocks = duration.ToSystemTicks(System.CyclesPerSecond);

        for (var i = 0; i < clocks && !Stopped; i++)
        {
            var previousPC = LastPC;

            TickSystem();
            TotalTicks++;

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
                if (_runToAddress == LastPC)
                {
                    _runToAddress = null;
                    Stopped = true;
                }
                else if (Breakpoints.ShouldBreak(LastPC))
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
        // The gap since the previous fetch is how long *that* instruction
        // actually took - recorded against its address, not this new one,
        // and skipped on the very first fetch since there's no previous one
        // to charge it to.
        if (_hasPreviousExecution)
        {
            LastExecutionCycles[_previousExecutionAddress] = (int)(TotalTicks - _previousExecutionTick);
        }

        _previousExecutionAddress = address;
        _previousExecutionTick = TotalTicks;
        _hasPreviousExecution = true;

        LastPC = address;

        Disassembler.OnAddressExecuting(address);
    }
}
