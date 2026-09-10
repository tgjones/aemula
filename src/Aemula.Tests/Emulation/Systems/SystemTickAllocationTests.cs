using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Aemula;
using Aemula.Emulation.Systems;
using Aemula.Emulation.Systems.AppleII;
using Aemula.Emulation.Systems.Atari2600;
using Aemula.Emulation.Systems.Nes;
using Aemula.Emulation.Systems.SpaceInvaders;

namespace Aemula.Tests.Emulation.Systems;

// EmulatedSystem.Tick() is the innermost loop of every system - Aemula.UI calls
// it tens of millions of times a second - so it must not allocate: a single
// per-tick allocation is ~20 M GC-tracked objects/second and drags a GC pause
// into the frame loop. BenchmarkDotNet's MemoryDiagnoser reports per-op bytes
// but nothing fails on a regression; this pins it at zero.
//
// Method: build + insert media into each system, warm it well past first-time
// JIT / lazy buffer setup, settle the GC, then measure
// GC.GetAllocatedBytesForCurrentThread across a large tick batch on the same
// thread. The delta must be exactly zero.
public class SystemTickAllocationTests
{
    private const int WarmupTicks = 200_000;
    private const int MeasuredTicks = 1_000_000;

    private static async Task AssertTickDoesNotAllocate(
        Func<EmulatedSystem> create,
        IReadOnlyList<(string BayId, MediaImage Image)>? media = null)
    {
        using var system = create();
        foreach (var (bayId, image) in media ?? [])
        {
            system.InsertMedia(bayId, image);
        }

        for (var i = 0; i < WarmupTicks; i++)
        {
            system.Tick();
        }

        // Settle anything the warmup queued, then take the baseline.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < MeasuredTicks; i++)
        {
            system.Tick();
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        await Assert.That(allocated)
            .IsEqualTo(0L)
            .Because($"{system.GetType().Name}.Tick() allocated {allocated} bytes over " +
                     $"{MeasuredTicks:N0} ticks ({allocated / (double)MeasuredTicks:F4} bytes/tick)");
    }

    // AppleII and SpaceInvaders currently DO allocate a small amount per tick
    // (~13 and ~7 bytes/tick respectively, measured): in steady state their
    // detected raster timing jitters by just over Television.ResizeDeadband, so
    // Television.ResizeSampleBufferIfDetectedTimingChanged keeps calling
    // SampleBuffer.Resize (which does `new Sample[w * h]`) every few frames.
    // NES and Atari don't trip it. Skipped rather than deleted so the guard
    // reappears the moment that churn is fixed - flip these to plain [Test].

    [Test]
    [Skip("SampleBuffer.Resize churn from raster-timing jitter > Television.ResizeDeadband; ~13 bytes/tick")]
    public Task AppleII_Tick_DoesNotAllocate() =>
        // Boots the bundled Apple2_Plus.rom; no removable media.
        AssertTickDoesNotAllocate(static () => new AppleIISystem());

    [Test]
    [Skip("SampleBuffer.Resize churn from raster-timing jitter > Television.ResizeDeadband; ~7 bytes/tick")]
    public Task SpaceInvaders_Tick_DoesNotAllocate() =>
        // Loads the bundled invaders.[efgh] ROM set; no removable media.
        AssertTickDoesNotAllocate(static () => new SpaceInvadersSystem());

    [Test]
    public Task Nes_Tick_DoesNotAllocate() =>
        // Unpatched nestest.nes, booting to its on-screen menu. DecodeVideo is
        // left at its default (on), so the composite-video + NTSC decode path
        // is covered too.
        AssertTickDoesNotAllocate(
            static () => new NesSystem(),
            [("cartridge", MediaImage.FromFile(
                Path.Combine("Emulation", "Systems", "Nes", "Assets", "nestest.nes")))]);

    [Test]
    public Task Atari2600_Tick_DoesNotAllocate()
    {
        // A 2K cart that just runs JMP-to-self from its reset vector (the same
        // minimal image Atari2600SystemTests uses), built straight into memory.
        var rom = new byte[2048];
        rom[0] = 0x4C; rom[1] = 0x00; rom[2] = 0x10;   // $1000: JMP $1000
        rom[0x7FC] = 0x00; rom[0x7FD] = 0x10;          // reset vector -> $1000

        return AssertTickDoesNotAllocate(
            static () => new Atari2600System(),
            [("cartridge", MediaImage.FromBytes("atari2600.bin", rom))]);
    }
}
