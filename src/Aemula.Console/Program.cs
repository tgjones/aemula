using System;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

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
        var (systemName, framesRequested, romPath) = ParseArgs(args);

        if (!SystemRegistry.Systems.TryGetValue(systemName, out var createSystem))
        {
            throw new ArgumentException($"Unknown system '{systemName}'. Supported systems: {string.Join(", ", SystemRegistry.Systems.Keys)}.");
        }

        var system = createSystem();

        var stopwatch = Stopwatch.StartNew();

        system.LoadProgram(romPath);
        var result = FrameRunner.Run(system, framesRequested);

        stopwatch.Stop();

        // Only this one line goes to stdout, so a caller can pipe straight into jq
        // without filtering out progress/status noise first.
        var summary = new JsonObject
        {
            ["system"] = systemName,
            ["framesRequested"] = framesRequested,
            ["framesRun"] = result.FramesRun,
            ["cyclesExecuted"] = result.CyclesExecuted,
            ["elapsedMs"] = stopwatch.Elapsed.TotalMilliseconds,
            ["screenshots"] = new JsonArray(),
        };

        SystemConsole.WriteLine(summary.ToJsonString());
    }

    private static (string SystemName, int FramesRequested, string RomPath) ParseArgs(string[] args)
    {
        string? systemName = null;
        int? framesRequested = null;
        var romPath = "";

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
            throw new ArgumentException("Usage: aemula-console <system> --frames <n> [--rom <path>]");
        }

        if (framesRequested == null)
        {
            throw new ArgumentException("--frames <n> is required.");
        }

        return (systemName, framesRequested.Value, romPath);
    }
}
