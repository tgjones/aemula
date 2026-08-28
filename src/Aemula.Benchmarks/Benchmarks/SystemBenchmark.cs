using Aemula;
using BenchmarkDotNet.Attributes;

namespace Aemula.Benchmarks;

// One measured invocation = a fixed number of EmulatedSystem.Tick() calls
// against a fixed workload, chosen (per system, in SystemSpecs) to be large
// enough that every periodic codepath fires many times: sub-frame video/audio
// dividers, per-frame sync regions, interrupts, the 60 Hz timer tick, and the
// frame boundary itself.
//
// This is the raw core path - no Debugger, so no breakpoint / step-mode /
// disassembler overhead (that delta is DebuggerOverheadBenchmark's job). For
// systems that drive video generation from Tick() (Apple II, Atari 2600, Space
// Invaders) that work, including feeding Television.Decode, is included here.
//
// BenchmarkDotNet does not discover [Benchmark] methods inherited from an
// abstract base, so each concrete system gets its own one-line Tick() that
// calls RunTicks(). Keeping a class per system also makes --filter '*Chip8*'
// etc. behave the way you'd expect.
public abstract class SystemBenchmark
{
    private SystemSpec _spec = null!;
    private EmulatedSystem _system = null!;

    private protected abstract string SystemName { get; }

    // Read by RealtimeBudgetColumn (via a throwaway instance) to turn the mean
    // time into a "% of real-time budget" figure.
    public ulong NominalCyclesPerSecond => SystemSpecs.Get(SystemName).CyclesPerSecond;
    public int TicksPerInvocation => SystemSpecs.Get(SystemName).TicksPerInvocation;

    [GlobalSetup]
    public void Setup()
    {
        _spec = SystemSpecs.Get(SystemName);
        _system = _spec.Create();
        _system.LoadProgram(_spec.WorkloadPath());

        for (var i = 0; i < _spec.WarmupTicks; i++)
        {
            _system.Tick();
        }
    }

    protected long RunTicks()
    {
        var system = _system;
        var probe = _spec.Probe;
        var n = _spec.TicksPerInvocation;

        // XOR a cheap piece of observable state in periodically so the JIT can't
        // prove the loop dead and elide it; returning the value keeps it live.
        long sink = 0;
        for (var i = 0; i < n; i++)
        {
            system.Tick();
            if ((i & 0x3FF) == 0)
            {
                sink ^= probe(system);
            }
        }

        return sink;
    }
}

public class AppleIIBenchmark : SystemBenchmark
{
    private protected override string SystemName => "appleii";

    [Benchmark]
    public long Tick() => RunTicks();
}

public class Atari2600Benchmark : SystemBenchmark
{
    private protected override string SystemName => "atari2600";

    [Benchmark]
    public long Tick() => RunTicks();
}

public class Chip8Benchmark : SystemBenchmark
{
    private protected override string SystemName => "chip8";

    [Benchmark]
    public long Tick() => RunTicks();
}

public class SpaceInvadersBenchmark : SystemBenchmark
{
    private protected override string SystemName => "spaceinvaders";

    [Benchmark]
    public long Tick() => RunTicks();
}
