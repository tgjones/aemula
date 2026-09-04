using System;
using System.Diagnostics;
using Aemula.Emulation.Systems;

namespace Aemula.Benchmarks;

// A no-BenchmarkDotNet entry point for sampling profilers. BDN spawns its own
// measurement process and rewrites the workload, which makes `dotnet-trace
// collect -- <exe>` attach to the wrong thing; this runs one system's warmup
// plus a flat Tick() loop in *this* process so a profiler can wrap it directly:
//
//   dotnet build -c Release src/Aemula.Benchmarks
//   dotnet-trace collect --providers Microsoft-DotNETCore-SampleProfiler \
//     -o nes.nettrace -- \
//     src/Aemula.Benchmarks/bin/Release/net10.0/Aemula.Benchmarks profile nes 20
//   dotnet-trace report nes.nettrace topN -n 30
//
// The trailing number is seconds to run (default 15). AEMULA_BENCH_NES_ROM is
// honoured here too. It also prints ns/tick, so it works as a quick between-runs
// sanity check without waiting on a full BDN job.
internal static class ProfileHarness
{
    public static int Run(string[] args)
    {
        var name = args.Length > 1 ? args[1] : "nes";
        var seconds = args.Length > 2 && double.TryParse(args[2], out var s) ? s : 15.0;

        var spec = SystemSpecs.Get(name);
        var system = EmulatedSystems.FindById(name)!.Create();
        system.LoadProgram(spec.WorkloadPath());

        Console.Error.WriteLine($"[profile] {name}: warming {spec.WarmupTicks:N0} ticks...");
        for (var i = 0; i < spec.WarmupTicks; i++)
        {
            system.Tick();
        }

        Console.Error.WriteLine($"[profile] {name}: running for {seconds:0.#}s...");
        long ticks = 0;
        long sink = 0;
        var sw = Stopwatch.StartNew();
        var budget = TimeSpan.FromSeconds(seconds);
        while (sw.Elapsed < budget)
        {
            // Batches of 4096 so the clock read isn't in the hot loop.
            for (var i = 0; i < 4096; i++)
            {
                system.Tick();
            }
            ticks += 4096;
            sink ^= spec.Probe(system);
        }
        sw.Stop();

        var nsPerTick = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / ticks;
        var achievedHz = ticks / sw.Elapsed.TotalSeconds;
        var realtimePct = achievedHz / system.CyclesPerSecond * 100.0;
        Console.Error.WriteLine(
            $"[profile] {name}: {ticks:N0} ticks in {sw.Elapsed.TotalSeconds:0.00}s = " +
            $"{nsPerTick:0.00} ns/tick, {realtimePct:0}% of real-time (sink={sink})");
        return 0;
    }
}
