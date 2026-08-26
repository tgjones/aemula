using System;
using Aemula.Emulation.Output;

namespace Aemula.Console;

public readonly record struct FrameRunResult(int FramesRun, ulong CyclesExecuted);

public static class FrameRunner
{
    // Straight system.Tick() loop - no Debugger involved, so no breakpoint checks/
    // step-mode/disassembler overhead. Same "raw Tick()" style as
    // Aemula.Benchmarks's own tick benchmark.
    public static FrameRunResult Run(EmulatedSystem system, int requestedFrames)
    {
        var television = ((IHasTelevision)system).Television;

        var previousRow = television.CurrentRow;
        var framesCompleted = 0;
        var cycles = 0UL;

        // Safety cap: if a system's signal never locks to a frame boundary, this
        // stops a runaway infinite loop instead of hanging forever - 10x the nominal
        // cycles/frame is comfortably more than any real self-calibration settling
        // time seen in this codebase's own Television tests.
        var maxCycles = system.CyclesPerSecond / 60UL * (ulong)requestedFrames * 10UL;

        while (framesCompleted < requestedFrames)
        {
            system.Tick();
            cycles++;

            // A "frame" is a wrap of CurrentRow back to a lower value than it just
            // was - tracks each system's real, self-calibrated timing (e.g. Apple
            // II's actual ~262.5 lines/frame) rather than assuming a nominal 60Hz
            // that individual systems don't exactly match.
            var currentRow = television.CurrentRow;
            if (currentRow < previousRow)
            {
                framesCompleted++;
            }
            previousRow = currentRow;

            if (cycles > maxCycles)
            {
                throw new InvalidOperationException(
                    $"{requestedFrames} frame(s) requested but the video signal never locked to a frame boundary after {cycles} cycles.");
            }
        }

        return new FrameRunResult(framesCompleted, cycles);
    }
}
