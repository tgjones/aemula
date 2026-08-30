using System;
using Aemula;
using Aemula.Debugging;
using BenchmarkDotNet.Attributes;

namespace Aemula.Benchmarks;

// The production path in Aemula.UI isn't EmulatedSystem.Tick() directly - it's
// Debugger.RunForDuration, called once per frame, which adds a per-tick
// breakpoint check, step-mode check and disassembler update on top of the
// system. Both methods here do the same system's TicksPerInvocation worth of
// work (RawTick directly, ViaDebugger in a handful of RunForDuration chunks the
// way the UI's frame loop does), so the Ratio column reads directly as "cost of
// the debugger scaffolding" and the Allocated column shows what it costs the GC.
//
// Free-run configuration matches DisassemblyWindow's "Continue": step mode
// disabled (ActiveStepModeIndex = -1), not stopped, no breakpoints set.
public class DebuggerOverheadBenchmark
{
    // How many RunForDuration calls make up one invocation - mimics the UI
    // calling it once per rendered frame rather than once for the whole budget.
    private const int Chunks = 4;

    [Params("appleii", "atari2600", "spaceinvaders")]
    public string SystemName = "";

    private EmulatedSystem _system = null!;
    private Debugger _debugger = null!;
    private int _ticksPerInvocation;
    private TimeSpan _chunkDuration;

    [GlobalSetup]
    public void Setup()
    {
        var spec = SystemSpecs.Get(SystemName);

        _system = spec.Create();
        _system.LoadProgram(spec.WorkloadPath());
        for (var i = 0; i < spec.WarmupTicks; i++)
        {
            _system.Tick();
        }

        _ticksPerInvocation = spec.TicksPerInvocation;

        // Duration that RunForDuration turns back into ~TicksPerInvocation/Chunks
        // ticks (it does duration -> ticks internally via CyclesPerSecond), so
        // the two benchmarks execute the same amount of emulated time.
        var secondsPerChunk = _ticksPerInvocation / (double)Chunks / spec.CyclesPerSecond;
        _chunkDuration = TimeSpan.FromSeconds(secondsPerChunk);

        _debugger = _system.CreateDebugger()
            ?? throw new InvalidOperationException($"{SystemName} has no debugger.");
        _debugger.ActiveStepModeIndex = -1;
        _debugger.Stopped = false;
    }

    [Benchmark(Baseline = true)]
    public void RawTick()
    {
        var system = _system;
        for (var i = 0; i < _ticksPerInvocation; i++)
        {
            system.Tick();
        }
    }

    [Benchmark]
    public void ViaDebugger()
    {
        var debugger = _debugger;
        debugger.Stopped = false;

        for (var i = 0; i < Chunks; i++)
        {
            debugger.RunForDuration(_chunkDuration);
        }
    }
}
