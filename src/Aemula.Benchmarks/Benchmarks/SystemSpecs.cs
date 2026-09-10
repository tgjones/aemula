using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Aemula.Emulation.Systems;

namespace Aemula.Benchmarks;

// Single source of truth for what each system's benchmark workload is: where
// its ROM comes from, how far to warm it before measuring, and how many
// Tick()s make up one measured invocation. Both the per-system SystemBenchmark
// classes and DebuggerOverheadBenchmark read from here so the two never drift
// apart. Name matches an Id in Aemula.Emulation.Systems.EmulatedSystems, which
// is where a system's factory and real CyclesPerSecond live - this doesn't
// duplicate either.
internal sealed record SystemSpec(
    string Name,
    Func<IReadOnlyList<(string BayId, MediaImage Image)>> Media,
    int WarmupTicks,
    int TicksPerInvocation,
    Func<EmulatedSystem, long> Probe);

internal static class SystemSpecs
{
    // Tick budgets are expressed as "≈ N frames" using each system's own
    // CyclesPerSecond / 60. One frame is enough to hit every periodic path
    // (sub-frame video/audio dividers, per-frame VSYNC/VBLANK/overscan regions,
    // interrupts, the 60 Hz timer tick); ≈2 frames also crosses a field
    // boundary so odd/even-frame handling is covered.
    private static long TelevisionRow(EmulatedSystem system) => system.Television.CurrentRow;

    public static readonly IReadOnlyList<SystemSpec> All =
    [
        new SystemSpec(
            "appleii",
            static () => [], // Boots the bundled Apple2_Plus.rom; no removable media
            WarmupTicks: 240_000,          // ≈1 frame: past reset, into the steady text screen
            TicksPerInvocation: 480_000,   // ≈2 frames
            TelevisionRow),

        new SystemSpec(
            "applei",
            static () => [], // The Monitor ROM is fixed; no removable media
            WarmupTicks: 240_000,          // ≈1 frame: past reset, into WozMon's idle prompt loop
            TicksPerInvocation: 480_000,   // ≈2 frames
            TelevisionRow),

        new SystemSpec(
            "atari2600",
            Workloads.Atari2600Kernel,
            WarmupTicks: 120_000,          // ≈2 frames: past the RAM-clear loop, into steady raster
            TicksPerInvocation: 120_000,   // ≈2 frames
            TelevisionRow),

        new SystemSpec(
            "nes", // DecodeVideo stays on: the NTSC decode FIR is the hot path
                   // and Aemula.UI always runs it. This is the faithful "why is
                   // it below real-time" workload.
            Workloads.NesRom,
            WarmupTicks: 3_600_000,        // ≈10 frames: past the power-on RAM clear / init, into the steady per-frame NMI loop
            TicksPerInvocation: 720_000,   // ≈2 frames (~357954 ticks/frame; crosses a field boundary)
            TelevisionRow),

        new SystemSpec(
            "spaceinvaders",
            static () => [], // Loads the bundled invaders.[efgh]; no removable media
            WarmupTicks: 340_000,          // ≈1 frame: into attract mode
            TicksPerInvocation: 680_000,   // ≈2 frames (covers both the mid-screen and VBLANK IRQs)
            TelevisionRow),
    ];

    public static SystemSpec Get(string name) =>
        All.FirstOrDefault(s => s.Name == name)
        ?? throw new ArgumentException($"No benchmark spec for system '{name}'. Known: {string.Join(", ", All.Select(s => s.Name))}.");
}

// Cartridge images for the systems whose workload isn't shipped by the Aemula
// project itself. Built straight into memory as MediaImages and inserted into
// the "cartridge" bay before warmup.
internal static class Workloads
{
    public static IReadOnlyList<(string BayId, MediaImage Image)> Atari2600Kernel() =>
        [("cartridge", MediaImage.FromBytes("aemula-bench-atari2600.bin", Atari2600TestKernel.Image))];

    // NES workload ROM. Set AEMULA_BENCH_NES_ROM to point the benchmark at any
    // local .nes file (e.g. a real game, to reproduce a below-real-time report);
    // otherwise the bundled rendering ROM embedded from the Aemula.Tests asset
    // tree is used.
    public static IReadOnlyList<(string BayId, MediaImage Image)> NesRom()
    {
        var overridePath = Environment.GetEnvironmentVariable("AEMULA_BENCH_NES_ROM");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return [("cartridge", MediaImage.FromFile(overridePath))];
        }

        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("nes-workload.nes")
            ?? throw new InvalidOperationException("Embedded nes-workload.nes is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return [("cartridge", MediaImage.FromBytes("nes-workload.nes", buffer.ToArray()))];
    }
}
