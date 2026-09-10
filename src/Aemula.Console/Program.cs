using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aemula;
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
        var options = ParseArgs(args);

        if (options.ListSlotsSystem is { } listSlotsSystem)
        {
            PrintSlots(listSlotsSystem);
            return;
        }

        var (systemName, framesRequested, romPath, screenshotPath, screenshotEvery, inputSpec, traceTiming) =
            (options.SystemName!, options.FramesRequested!.Value, options.RomPath, options.ScreenshotPath,
                options.ScreenshotEvery, options.InputSpec, options.TraceTiming);

        var descriptor = EmulatedSystems.FindById(systemName)
            ?? throw new ArgumentException($"Unknown system '{systemName}'. Supported systems: {string.Join(", ", EmulatedSystems.All.Select(d => d.Id))}.");

        // A bad --slot slot/card id throws out of ResolvedAgainst here, into the
        // top-level catch.
        using var rig = descriptor.Build(new ExpansionSlotConfiguration(options.SlotChoices));
        var television = rig.Television;

        // Parsed after the rig exists so the script's control tokens can be
        // validated against its InputKeyBindings and console controls (the
        // cassette deck's tape-play / tape-rewind included).
        var inputScript = inputSpec != null ? InputScript.Parse(inputSpec, rig) : null;

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
            inputScript?.ApplyForFrame(rig, framesCompleted);
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

        // A .wav romPath is routed to the cassette deck (see Rig.LoadProgram);
        // an --input script presses tape-play and types the load command, the
        // same way it would drive any other console control.
        rig.LoadProgram(romPath);

        // Frame 0 fires before the run so "0:reset+" and friends take effect
        // from the very first emulated frame.
        inputScript?.ApplyForFrame(rig, 0);
        var result = FrameRunner.Run(rig.System, framesRequested, OnFrameCompleted);

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

    // Prints one system's expansion slots and the cards each accepts, then
    // exits - `--list-slots <system>` with nothing else required.
    private static void PrintSlots(string systemName)
    {
        var descriptor = EmulatedSystems.FindById(systemName)
            ?? throw new ArgumentException($"Unknown system '{systemName}'. Supported systems: {string.Join(", ", EmulatedSystems.All.Select(d => d.Id))}.");

        if (descriptor.Slots.Count == 0)
        {
            SystemConsole.WriteLine($"{descriptor.Id} ({descriptor.DisplayName}) has no configurable expansion slots.");
            return;
        }

        SystemConsole.WriteLine($"{descriptor.Id} ({descriptor.DisplayName}) expansion slots:");
        foreach (var slot in descriptor.Slots)
        {
            var @default = slot.DefaultCardId ?? "none";
            SystemConsole.WriteLine($"  {slot.Id}  ({slot.DisplayName}) - default: {@default}");
            SystemConsole.WriteLine("    none  (empty)");
            foreach (var card in slot.Cards)
            {
                var summary = card.Summary is { } s ? $" - {s}" : "";
                SystemConsole.WriteLine($"    {card.Id}  {card.DisplayName}{summary}");
            }
        }

        SystemConsole.WriteLine("");
        SystemConsole.WriteLine("Select with --slot <slot>=<card> (or --slot <slot>=none), repeatable.");
    }

    private sealed record ConsoleOptions(
        string? SystemName,
        int? FramesRequested,
        string RomPath,
        string? ScreenshotPath,
        int? ScreenshotEvery,
        string? InputSpec,
        bool TraceTiming,
        IReadOnlyList<(string SlotId, string? CardId)> SlotChoices,
        string? ListSlotsSystem);

    private static ConsoleOptions ParseArgs(string[] args)
    {
        string? systemName = null;
        int? framesRequested = null;
        var romPath = "";
        string? screenshotPath = null;
        int? screenshotEvery = null;
        string? inputSpec = null;
        var traceTiming = false;
        var slotChoices = new List<(string SlotId, string? CardId)>();
        string? listSlotsSystem = null;

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

                case "--slot":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("--slot requires a value of the form <slot>=<card> (card may be 'none').");
                    }
                    slotChoices.Add(ParseSlotChoice(args[i + 1]));
                    i += 2;
                    break;

                case "--list-slots":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("--list-slots requires a system name.");
                    }
                    listSlotsSystem = args[i + 1];
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

        // --list-slots is a standalone query: it names its own system and needs
        // nothing else.
        if (listSlotsSystem != null)
        {
            return new ConsoleOptions(null, null, romPath, null, null, null, false, slotChoices, listSlotsSystem);
        }

        if (systemName == null)
        {
            throw new ArgumentException(
                "Usage: aemula-console <system> --frames <n> [--rom <path>] " +
                "[--screenshot <path>] [--screenshot-every <n>] " +
                "[--input \"<frame>:<token>+/-  or  <frame>:\\\"typed text\\\", ...\"] " +
                "[--slot <slot>=<card>] [--trace-timing]  |  aemula-console --list-slots <system>");
        }

        if (framesRequested == null)
        {
            throw new ArgumentException("--frames <n> is required.");
        }

        if (screenshotEvery != null && screenshotPath == null)
        {
            throw new ArgumentException("--screenshot-every requires --screenshot.");
        }

        return new ConsoleOptions(
            systemName, framesRequested.Value, romPath, screenshotPath, screenshotEvery, inputSpec, traceTiming,
            slotChoices, null);
    }

    // "<slot>=<card>", where a card of "none" (or empty) means the empty slot.
    private static (string SlotId, string? CardId) ParseSlotChoice(string spec)
    {
        var eq = spec.IndexOf('=');
        if (eq <= 0)
        {
            throw new ArgumentException($"--slot value '{spec}' must be of the form <slot>=<card> (card may be 'none').");
        }

        var slotId = spec[..eq];
        var cardId = spec[(eq + 1)..];
        return (slotId, cardId is "" or "none" ? null : cardId);
    }
}
