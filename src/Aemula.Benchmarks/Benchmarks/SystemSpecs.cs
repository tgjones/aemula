using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Aemula.Emulation.Systems.AppleII;
using Aemula.Emulation.Systems.Atari2600;
using Aemula.Emulation.Systems.SpaceInvaders;

namespace Aemula.Benchmarks;

// Single source of truth for what each system's benchmark workload is: how to
// build the system, where its ROM comes from, how far to warm it before
// measuring, and how many Tick()s make up one measured invocation. Both the
// per-system SystemBenchmark classes and DebuggerOverheadBenchmark read from
// here so the two never drift apart.
internal sealed record SystemSpec(
    string Name,
    Func<EmulatedSystem> Create,
    Func<string> WorkloadPath,
    ulong CyclesPerSecond,
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
    private const ulong AppleIIHz = 14_318_180;
    private const ulong Atari2600Hz = 3_580_000;
    private const ulong SpaceInvadersHz = 19_968_000;

    private static long TelevisionRow(EmulatedSystem system) => system.Television.CurrentRow;

    public static readonly IReadOnlyList<SystemSpec> All =
    [
        new SystemSpec(
            "appleii",
            static () => new AppleIISystem(),
            static () => "", // LoadProgram ignores the path; boots the bundled Apple2_Plus.rom
            AppleIIHz,
            WarmupTicks: 240_000,          // ≈1 frame: past reset, into the steady text screen
            TicksPerInvocation: 480_000,   // ≈2 frames
            TelevisionRow),

        new SystemSpec(
            "atari2600",
            static () => new Atari2600System(),
            Workloads.Atari2600Kernel,
            Atari2600Hz,
            WarmupTicks: 120_000,          // ≈2 frames: past the RAM-clear loop, into steady raster
            TicksPerInvocation: 120_000,   // ≈2 frames
            TelevisionRow),

        // No "nes" spec: NesSystem's PPU/mapper support is still a stub, so
        // nestest runs off the rails past its automated section ($C66E) with
        // nothing valid to execute. Re-add once the NES is a real perf target.

        new SystemSpec(
            "spaceinvaders",
            static () => new SpaceInvadersSystem(),
            static () => "", // LoadProgram ignores the path; loads the bundled invaders.[efgh]
            SpaceInvadersHz,
            WarmupTicks: 340_000,          // ≈1 frame: into attract mode
            TicksPerInvocation: 680_000,   // ≈2 frames (covers both the mid-screen and VBLANK IRQs)
            TelevisionRow),
    ];

    public static SystemSpec Get(string name) =>
        All.FirstOrDefault(s => s.Name == name)
        ?? throw new ArgumentException($"No benchmark spec for system '{name}'. Known: {string.Join(", ", All.Select(s => s.Name))}.");
}

// ROM images that aren't shipped by the Aemula project itself are materialised
// to stable temp-file paths here, because every system's LoadProgram takes a
// file path. The names are fixed (not random) so a run can be inspected or
// re-fed to Aemula.Console by hand.
internal static class Workloads
{
    public static string Atari2600Kernel()
    {
        var path = Path.Combine(Path.GetTempPath(), "aemula-bench-atari2600.bin");
        File.WriteAllBytes(path, Atari2600TestKernel.Image);
        return path;
    }
}
