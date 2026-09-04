using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aemula.Emulation.Systems;

namespace Aemula.Console;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            Run(args);
            return 0;
        }
        catch (Exception ex)
        {
            // Single top-level catch: this is the CLI's error boundary (bad system
            // name, bad ROM path, frame-detection timeout, etc. all land here) rather
            // than a place that needs its own defensive handling per failure mode.
            SystemConsole.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void Run(string[] args)
    {
        var (systemName, framesRequested, romPath, screenshotPath, screenshotEvery, inputSpec, traceTiming) = ParseArgs(args);

        var descriptor = EmulatedSystems.FindById(systemName)
            ?? throw new ArgumentException($"Unknown system '{systemName}'. Supported systems: {string.Join(", ", EmulatedSystems.All.Select(d => d.Id))}.");

        var system = descriptor.Create();
        var television = system.Television;

        // Parsed after the system exists so the script's control tokens can be
        // validated against that system's InputKeyBindings.
        var inputScript = inputSpec != null ? InputScript.Parse(inputSpec, system) : null;

        // ScreenshotWriter reads Sample.Region (via ComputeActiveVideoRowRange)
        // and Sample.Color out of SampleBuffer; Region is only populated when
        // capture is enabled. Left off for a screenshot-less run so a long
        // headless pass keeps the faster decode path.
        if (screenshotPath != null)
        {
            television.CaptureSampleDiagnostics = true;
        }

        var screenshotsWritten = new List<string>();

        // Zero-padded to a fixed 6 digits regardless of framesRequested - simpler
        // than sizing the width to the run (and lets one run's files sort
        // correctly alongside another's without repadding), at the cost of
        // looking odd only past a million frames, well beyond anything this tool
        // is used for today.
        void WritePeriodicScreenshot(int framesCompleted)
        {
            if (screenshotPath != null && screenshotEvery != null && framesCompleted % screenshotEvery.Value == 0)
            {
                var numberedPath = InsertFrameNumber(screenshotPath, framesCompleted);
                ScreenshotWriter.Write(television, numberedPath);
                screenshotsWritten.Add(numberedPath);
            }
        }

        // --trace-timing prints, per detected frame, the emulated tick count
        // for that frame alongside the decoder's own detected line/sample
        // geometry - to stderr, so stdout stays a single clean JSON line.
        // Meant for chasing frame-timing drift (a game whose tick/frame should
        // be constant but isn't).
        void OnFrameCompleted(int framesCompleted, ulong ticksThisFrame)
        {
            inputScript?.ApplyForFrame(system, framesCompleted);
            WritePeriodicScreenshot(framesCompleted);

            if (traceTiming)
            {
                SystemConsole.Error.WriteLine(
                    $"frame {framesCompleted,4}: ticks={ticksThisFrame,6} " +
                    $"lines/frame={television.DetectedLinesPerFrame:F2} " +
                    $"samples/line={television.DetectedSamplesPerLine:F2} " +
                    $"buf={television.SampleBuffer.Width}x{television.SampleBuffer.Height}");
            }
        }

        var stopwatch = Stopwatch.StartNew();

        system.LoadProgram(romPath);

        // Frame 0 fires before the run so "0:reset+" and friends take effect
        // from the very first emulated frame.
        inputScript?.ApplyForFrame(system, 0);
        var result = FrameRunner.Run(system, framesRequested, OnFrameCompleted);

        stopwatch.Stop();

        // The final screenshot is written to the exact --screenshot path (no
        // frame number inserted) after all periodic ones, whether or not
        // --screenshot-every ever fired.
        if (screenshotPath != null)
        {
            ScreenshotWriter.Write(television, screenshotPath);
            screenshotsWritten.Add(screenshotPath);
        }

        // Only this one line goes to stdout, so a caller can pipe straight into jq
        // without filtering out progress/status noise first.
        var summary = new JsonObject
        {
            ["system"] = systemName,
            ["framesRequested"] = framesRequested,
            ["framesRun"] = result.FramesRun,
            ["cyclesExecuted"] = result.CyclesExecuted,
            ["elapsedMs"] = stopwatch.Elapsed.TotalMilliseconds,
            ["screenshots"] = new JsonArray([.. screenshotsWritten.Select(path => (JsonNode?)JsonValue.Create(path))]),
        };

        SystemConsole.WriteLine(summary.ToJsonString());
    }

    // Inserts a zero-padded frame count before path's extension, e.g.
    // "out.png" -> "out.000060.png" - keeps periodic screenshots sorting and
    // scripting cleanly against the same base name the final --screenshot path
    // uses, without needing a separate output directory convention.
    private static string InsertFrameNumber(string path, int frameCount)
    {
        var directory = Path.GetDirectoryName(path);
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        var numberedFileName = $"{fileNameWithoutExtension}.{frameCount:D6}{extension}";

        return string.IsNullOrEmpty(directory) ? numberedFileName : Path.Combine(directory, numberedFileName);
    }

    private static (string SystemName, int FramesRequested, string RomPath, string? ScreenshotPath, int? ScreenshotEvery, string? InputSpec, bool TraceTiming) ParseArgs(string[] args)
    {
        string? systemName = null;
        int? framesRequested = null;
        var romPath = "";
        string? screenshotPath = null;
        int? screenshotEvery = null;
        string? inputSpec = null;
        var traceTiming = false;

        var i = 0;
        while (i < args.Length)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--frames":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("--frames requires a value.");
                    }
                    if (!int.TryParse(args[i + 1], out var frames) || frames <= 0)
                    {
                        throw new ArgumentException($"--frames must be a positive integer, got '{args[i + 1]}'.");
                    }
                    framesRequested = frames;
                    i += 2;
                    break;

                case "--rom":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("--rom requires a value.");
                    }
                    romPath = args[i + 1];
                    i += 2;
                    break;

                case "--screenshot":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("--screenshot requires a value.");
                    }
                    screenshotPath = args[i + 1];
                    i += 2;
                    break;

                case "--screenshot-every":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("--screenshot-every requires a value.");
                    }
                    if (!int.TryParse(args[i + 1], out var every) || every <= 0)
                    {
                        throw new ArgumentException($"--screenshot-every must be a positive integer, got '{args[i + 1]}'.");
                    }
                    screenshotEvery = every;
                    i += 2;
                    break;

                case "--input":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("--input requires a value.");
                    }
                    inputSpec = args[i + 1];
                    i += 2;
                    break;

                case "--trace-timing":
                    traceTiming = true;
                    i++;
                    break;

                default:
                    if (systemName != null)
                    {
                        throw new ArgumentException($"Unexpected argument '{arg}'.");
                    }
                    systemName = arg;
                    i++;
                    break;
            }
        }

        if (systemName == null)
        {
            throw new ArgumentException(
                "Usage: aemula-console <system> --frames <n> [--rom <path>] " +
                "[--screenshot <path>] [--screenshot-every <n>] " +
                "[--input \"<frame>:<token>,...\"] [--trace-timing]");
        }

        if (framesRequested == null)
        {
            throw new ArgumentException("--frames <n> is required.");
        }

        if (screenshotEvery != null && screenshotPath == null)
        {
            throw new ArgumentException("--screenshot-every requires --screenshot.");
        }

        return (systemName, framesRequested.Value, romPath, screenshotPath, screenshotEvery, inputSpec, traceTiming);
    }
}
