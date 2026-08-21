using System;
using System.Collections.Generic;
using System.Diagnostics;
using Aemula.Emulation.Output.Ntsc;
using Aemula.Emulation.Systems.AppleII;
using Aemula.Emulation.Systems.Atari2600;
using Aemula.Emulation.Systems.Chip8;
using Aemula.Emulation.Systems.Nes;
using Aemula.Emulation.Systems.SpaceInvaders;

namespace Aemula.Benchmarks;

// Headless perf harness for the emulation core, kept separate from Aemula.UI so it
// can be run and iterated on without touching SDL/ImGui or the app window at all -
// see the "let's set up performance iteration" conversation that added this.
//
// Isolates where per-frame time actually goes: raw EmulatedSystem.Tick() (CPU +
// video generation), Debugger.RunForDuration (adds breakpoints/step-mode/disassembler
// overhead on top), and Television.Decode alone (the NTSC decode pipeline, which runs
// unconditionally every tick regardless of whether TelevisionWindow is open).
public static class Program
{
    private static readonly Dictionary<string, Func<EmulatedSystem>> Systems = new()
    {
        { "appleii", () => new AppleIISystem() },
        { "atari2600", () => new Atari2600System() },
        { "chip8", () => new Chip8System() },
        { "nes", () => new NesSystem() },
        { "spaceinvaders", () => new SpaceInvadersSystem() },
    };

    private static readonly TimeSpan WarmupDuration = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan MeasureDuration = TimeSpan.FromSeconds(1);

    public static void Main(string[] args)
    {
        var systemArg = args.Length > 0 ? args[0] : "appleii";
        Console.WriteLine($"System: {systemArg} (build: {(IsOptimizedBuild() ? "optimized" : "DEBUG - results are not representative")})");
        Console.WriteLine();

        RunSystemTickBenchmark(systemArg);
        RunDebuggerRunForDurationBenchmark(systemArg);

        if (systemArg == "appleii")
        {
            RunTelevisionDecodeBenchmark();
        }
    }

    private static bool IsOptimizedBuild()
    {
#if DEBUG
        return false;
#else
        return true;
#endif
    }

    // Raw EmulatedSystem.Tick() loop - no Debugger involved, so no breakpoint
    // checks/step-mode/disassembler overhead. This is CPU emulation + whatever the
    // system does per tick (e.g. AppleIISystem also runs video generation and feeds
    // Television.Decode from here - see AppleIISystem.CompositeVideo.cs).
    private static void RunSystemTickBenchmark(string systemArg)
    {
        var system = Systems[systemArg]();
        system.LoadProgram("");

        Run("EmulatedSystem.Tick() (raw)", system.CyclesPerSecond, tick: system.Tick);
    }

    // The actual production path: Debugger.RunForDuration, called with ~17ms chunks
    // the same way Aemula.UI's main loop calls it once per frame, free-running
    // (Stopped = false, as if the user pressed Run in DisassemblyWindow). Compare
    // against the raw Tick() benchmark above to see how much the debugger
    // scaffolding (breakpoints, step mode, disassembler) costs on top of the system
    // itself. Cycles actually executed are counted via Ticked rather than assumed,
    // since RunForDuration can execute fewer than requested (e.g. Stopped becoming
    // true mid-call) - same reasoning as Aemula.UI's own perf counters.
    private static void RunDebuggerRunForDurationBenchmark(string systemArg)
    {
        var system = Systems[systemArg]();
        system.LoadProgram("");

        var debugger = system.CreateDebugger();
        if (debugger == null)
        {
            Console.WriteLine($"{systemArg} has no debugger - skipping Debugger.RunForDuration benchmark.");
            return;
        }

        // -1 disables step-mode stopping entirely, i.e. true free-run - same as
        // DisassemblyWindow's "Continue" button. Without this, the default
        // ActiveStepModeIndex (1, set in Debugger's own constructor) stops execution
        // again almost immediately.
        debugger.ActiveStepModeIndex = -1;
        debugger.Stopped = false;

        var cycles = 0L;
        debugger.Ticked += () => cycles++;

        var frameChunk = TimeSpan.FromMilliseconds(17);

        var warmupStopwatch = Stopwatch.StartNew();
        while (warmupStopwatch.Elapsed < WarmupDuration)
        {
            debugger.RunForDuration(frameChunk);
        }

        cycles = 0;
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < MeasureDuration)
        {
            debugger.RunForDuration(frameChunk);
        }
        stopwatch.Stop();

        Report("Debugger.RunForDuration (production path, 17ms chunks)", system.CyclesPerSecond, cycles, stopwatch.Elapsed);
    }

    // Television.Decode alone, fed synthetic samples with no CPU/system behind it at
    // all - isolates the NTSC decode pipeline's own cost (sync separator, raster
    // oscillators, color-burst PLL, YIQ decoder), which today runs on every sample
    // regardless of whether TelevisionWindow is open (see Television.cs's Decode).
    // Then each of those four stages alone, so it's clear which one to spend
    // optimization effort on rather than guessing.
    private static void RunTelevisionDecodeBenchmark()
    {
        var nominalCyclesPerSecond = new AppleIISystem().CyclesPerSecond;

        var television = new Aemula.Emulation.Output.Television();
        var random = new Random(0);
        var sample = (byte)0;

        Run("Television.Decode (isolated, full pipeline)", nominalCyclesPerSecond, tick: () =>
        {
            television.Decode(sample);
            sample = (byte)random.Next(256);
        });

        // Synthetic but signal-shaped inputs for each stage below (slowly varying
        // column/phase rather than constants) so branch-heavy code isn't measured
        // taking the same path every call - matters less for correctness here than
        // for the earlier benchmarks, since these stages aren't chained to each
        // other's real output, but keeps each number honest about branchy costs.
        var syncSeparator = new NtscSyncSeparator();
        Run("  NtscSyncSeparator.Process", nominalCyclesPerSecond, tick: () =>
        {
            syncSeparator.Process(sample);
            sample = (byte)random.Next(256);
        });

        var rasterOscillators = new NtscRasterOscillators();
        var hSyncToggle = false;
        Run("  NtscRasterOscillators.Process", nominalCyclesPerSecond, tick: () =>
        {
            hSyncToggle = !hSyncToggle;
            rasterOscillators.Process(hSyncToggle, false);
        });

        var colorBurstPll = new NtscColorBurstPll();
        double column = 0;
        Run("  NtscColorBurstPll.Process", nominalCyclesPerSecond, tick: () =>
        {
            colorBurstPll.Process(sample, column, blackLevel: 40, whiteLevel: 200);
            sample = (byte)random.Next(256);
            column = (column + 1) % 912;
        });

        var yiqDecoder = new NtscYiqDecoder();
        double phase = 0;
        Run("  NtscYiqDecoder.Process", nominalCyclesPerSecond, tick: () =>
        {
            yiqDecoder.Process(sample, phase, blackLevel: 40, whiteLevel: 200);
            sample = (byte)random.Next(256);
            phase = (phase + 0.1) % (2 * Math.PI);
        });
    }

    private static void Run(string name, ulong nominalCyclesPerSecond, Action tick)
    {
        var warmupStopwatch = Stopwatch.StartNew();
        while (warmupStopwatch.Elapsed < WarmupDuration)
        {
            tick();
        }

        long ticks = 0;
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < MeasureDuration)
        {
            // Batch between Elapsed checks so Stopwatch reads don't dominate at high tick rates.
            for (var i = 0; i < 10_000; i++)
            {
                tick();
            }
            ticks += 10_000;
        }
        stopwatch.Stop();

        Report(name, nominalCyclesPerSecond, ticks, stopwatch.Elapsed);
    }

    private static void Report(string name, ulong nominalCyclesPerSecond, long cyclesExecuted, TimeSpan elapsed)
    {
        var nsPerCycle = elapsed.TotalMilliseconds * 1_000_000.0 / cyclesExecuted;
        var nominalNsPerCycle = 1_000_000_000.0 / nominalCyclesPerSecond;

        // How many real ms it takes to simulate one nominal video frame's worth
        // (17ms) of emulated time at this rate - directly comparable to the "ms per
        // frame" figure shown in Aemula.UI's main menu bar.
        var nominalCyclesPerFrame = 0.017 * nominalCyclesPerSecond;
        var msPerNominalFrame = nominalCyclesPerFrame * nsPerCycle / 1_000_000.0;

        Console.WriteLine($"{name}:");
        Console.WriteLine($"  {nsPerCycle:F1} ns/cycle achieved vs {nominalNsPerCycle:F1} ns/cycle nominal budget ({nominalNsPerCycle / nsPerCycle:P0} of budget)");
        Console.WriteLine($"  ~{msPerNominalFrame:F2} ms to simulate one 17ms nominal frame");
        Console.WriteLine();
    }
}
