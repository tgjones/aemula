# Aemula.Console — Implementation Plan

## Goal

A new, ImGui/SDL-free console project that can run any Television-backed
system headlessly for a requested number of frames and, on request, dump
`Television.SampleBuffer` to a PNG. Its primary user is Claude Code itself,
iterating on emulation/video code and wanting a fast, scriptable way to
"run this ROM for N frames and show me what it looks like" without a GUI
window in the loop at all.

Scope for this pass: run + screenshot only. Trace output and any other
diagnostics are explicitly deferred to a later plan.

## Decisions made before implementing

- **Systems supported:** only the three systems that expose a `Television`
  today — `AppleIISystem`, `Atari2600System`, `SpaceInvadersSystem`. Frame
  counting (below) is defined in terms of `Television.CurrentRow`, which
  `NesSystem`/`Chip8System` have no equivalent of, so they're out of scope
  here rather than half-supported. Extending this to other systems is a
  follow-up once they have some equivalent notion of "frame."
- **Frame counting:** a "frame" is a wrap of `Television.CurrentRow` back to
  a lower value than it just was, tracked by the console loop itself, not a
  new event on `Television`. This tracks each system's real, self-calibrated
  timing (e.g. Apple II's actual ~262.5 lines/frame) — the same notion of
  "frame" `TelevisionWindow` itself renders — rather than assuming a nominal
  60Hz that individual systems don't exactly match.
- **Screenshot content:** active-video-only, cropped, with the same 4:3
  vertical stretch `TelevisionWindow` applies by default. This is "what a
  real TV would show," which is what matters for eyeballing whether output
  looks right.
- **PNG encoding:** via `SixLabors.ImageSharp` (new dependency), rather than
  a hand-rolled encoder. This is the one part of the codebase where "build
  it from scratch like the chips" isn't the right tradeoff — PNG encoding
  has no emulation value and ImageSharp is a well-trodden, pure-managed,
  cross-platform choice.
- **Extras included now:** periodic screenshots during a run (not just one
  at the end), and a machine-readable one-line JSON summary on exit, both
  because the primary user is an agent scripting repeated runs, not a human
  reading prose output.

## Project layout

New `src/Aemula.Console/Aemula.Console.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Aemula\Aemula.csproj" />
    <PackageReference Include="SixLabors.ImageSharp" />
  </ItemGroup>
</Project>
```

Added to `Aemula.slnx` alongside the other four projects, and to
`Directory.Packages.props`'s `PackageVersion` list (pin to whatever's
current stable at implementation time).

Note: `Aemula.Console`'s namespace is `Aemula.Console`, which collides
textually with `System.Console` — unqualified `Console.WriteLine` inside
that namespace needs `System.Console.WriteLine` (or an alias) to resolve
unambiguously. Handled with a `global using SystemConsole = System.Console;`
in a small `GlobalUsings.cs`, used everywhere instead of the bare name.

Files:
- `Program.cs` — argument parsing, top-level error handling, wires the rest
  together.
- `SystemRegistry.cs` — the `Dictionary<string, Func<EmulatedSystem>>` for
  the 3 supported systems, same shape as `Aemula.UI/Program.cs` and
  `Aemula.Benchmarks/Program.cs`'s own copies of this table (existing
  duplication pattern in this codebase; not worth a shared abstraction for
  a 3-line dictionary).
- `FrameRunner.cs` — ticks a system, counting real frame boundaries via
  `Television.CurrentRow`, with a runaway-safety cap.
- `ScreenshotWriter.cs` — converts a cropped/stretched `SampleBuffer` region
  into an `Image<Rgba32>` and saves it as PNG.

## Core library changes

1. **`IHasTelevision`** — new tiny interface in
   `Aemula/Emulation/Output/IHasTelevision.cs`:

   ```csharp
   public interface IHasTelevision
   {
       Television Television { get; }
   }
   ```

   Implemented by `AppleIISystem`, `Atari2600System`, `SpaceInvadersSystem`
   (add `: IHasTelevision` to each `*.CompositeVideo.cs` partial, where the
   `Television` field already lives). Lets `Aemula.Console` (and anything
   else in the future) get at a system's `Television` generically instead
   of switching on concrete type.

2. **Extract the crop/stretch math out of `TelevisionWindow`** —
   `ComputeVerticalActiveRange` and `VerticalStretchFactor` are private
   `TelevisionWindow` methods today, but the screenshot writer needs exactly
   the same "which rows are really active video, and what vertical stretch
   makes this 4:3" logic. Move both onto `Television` itself as public
   members (they only ever read `SampleBuffer`/`ActiveVideoLengthSamples`,
   nothing ImGui-specific), and have `TelevisionWindow` call the new
   `Television` members instead of its own copies. One implementation, two
   consumers, instead of `Aemula.Console` re-deriving the same math
   independently and risking drift.

## CLI

```
aemula-console <system> [--rom <path>] --frames <n>
    [--screenshot <path>] [--screenshot-every <n>]
```

- `<system>` — positional, one of `appleii` / `atari2600` / `spaceinvaders`.
- `--rom <path>` — optional. Passed to `LoadProgram`; omitted means `""`,
  matching `Aemula.UI`'s own `programFilePath ?? ""` fallback. Only
  `Atari2600System` actually reads this path (a cartridge image) — the
  other two ignore it and load their fixed built-in ROMs, exactly as they
  do today. `Atari2600System.LoadProgram` throws if given `""`; that
  propagates as a clean top-level error rather than special-cased here.
- `--frames <n>` — required. Runs until `<n>` real frames (see above) have
  completed.
- `--screenshot <path>` — optional. Writes one PNG at the end of the run
  (after all `<n>` frames) to this exact path.
- `--screenshot-every <n>` — optional, requires `--screenshot`. Also writes
  a PNG every `<n>` frames during the run, numbered by inserting the frame
  count before `--screenshot`'s extension (`out.png` → `out.000060.png`,
  `out.000120.png`, ...). The final end-of-run screenshot at the exact
  `--screenshot` path is still written on top of that.

Top-level errors (bad system name, ROM load failure, frame-detection
timeout) are caught once in `Program.Main`, printed to stderr, exit code 1.
Everything else that isn't the final JSON summary line (progress, etc., if
any) goes to stderr too, so stdout is clean enough to pipe into `jq`.

On success, one JSON line to stdout, e.g.:

```json
{"system":"appleii","framesRequested":60,"framesRun":60,"cyclesExecuted":382200,"elapsedMs":41.2,"screenshots":["out.png"]}
```

## Frame-running mechanics

Straight `system.Tick()` loop — no `Debugger` involved (no breakpoints/step
mode needed for this tool), same "raw Tick()" style as
`Aemula.Benchmarks`'s own tick benchmark. Pseudocode:

```csharp
var television = ((IHasTelevision)system).Television;
var previousRow = television.CurrentRow;
var framesCompleted = 0;
var cycles = 0UL;

// Safety cap: if a system's signal never locks to a frame boundary, this
// stops a runaway infinite loop instead of hanging forever - 10x the
// nominal cycles/frame is comfortably more than any real self-calibration
// settling time seen in this codebase's own Television tests.
var maxCycles = (ulong)(system.CyclesPerSecond / 60UL) * requestedFrames * 10UL;

while (framesCompleted < requestedFrames)
{
    system.Tick();
    cycles++;

    var currentRow = television.CurrentRow;
    if (currentRow < previousRow)
    {
        framesCompleted++;
        MaybeWriteScreenshotEvery(framesCompleted);
    }
    previousRow = currentRow;

    if (cycles > maxCycles)
    {
        throw new InvalidOperationException(
            $"{requestedFrames} frames requested but the video signal never locked to a frame boundary after {cycles} cycles.");
    }
}
```

## Screenshot mechanics

`ScreenshotWriter.Write(Television television, string path)`:

1. Read `(verticalActiveStart, verticalActiveCount)` and the vertical
   stretch factor from the new `Television` members (see above).
2. Build an `Image<Rgba32>` sized
   `(int)television.ActiveVideoLengthSamples` ×
   `(int)MathF.Round(verticalActiveCount * stretchFactor)`.
3. For each output row, map back to the source sample row (nearest-
   neighbor is fine — this is a diagnostic screenshot, not a precision
   resample) and copy `ActiveVideoLengthSamples` samples starting at
   `ActiveVideoStartSamples`, taking `.Color` from each `Sample` straight
   into the `Rgba32` (same field TelevisionWindow's texture upload copies).
4. `image.SaveAsPng(path)`.

## Phased plan

**Phase 1 — Core library changes**
Add `IHasTelevision`, implement it on the 3 systems, extract
`ComputeVerticalActiveRange`/vertical-stretch math onto `Television`,
repoint `TelevisionWindow` at the new members. **Done when:** existing
`TelevisionWindow` behavior is pixel-identical (manual check — no automated
screenshot test exists yet, that's what this whole project is for) and
`Aemula.Tests` still passes for the touched systems.

**Phase 2 — Project scaffold + run loop**
New `Aemula.Console` project, `Aemula.slnx`/`Directory.Packages.props`
entries, `SystemRegistry`, `FrameRunner`, CLI parsing for `<system>`,
`--rom`, `--frames`. No screenshot yet — running and printing the JSON
summary (`screenshots: []`) is enough to call this phase done. **Done
when:** `aemula-console appleii --frames 60` runs to completion and prints
a summary with the right frame/cycle counts.

**Phase 3 — Screenshots**
`ScreenshotWriter`, `--screenshot`, `--screenshot-every`. **Done when:** a
screenshot of a booted Apple II (or Space Invaders attract mode) opens as a
correct-looking, correctly-cropped/stretched PNG.

## Open risks / questions

- **Frame-lock settling time is unmeasured for a cold start.** The 3
  systems' `Television` instances start from nominal timing and
  self-calibrate over the first several real frames (see `Television.cs`'s
  own remarks) — `--frames 1` very early after boot may land on a row-wrap
  detection that's still slightly off. Not expected to matter for typical
  use (asking for enough frames to reach a stable picture, e.g. an attract
  screen), but worth knowing if an early single-frame screenshot ever looks
  torn.
- **`--screenshot-every` numbering scheme** (zero-padded frame count
  inserted before the extension) is a reasonable default but arbitrary —
  flag if a different naming convention (e.g. a separate output directory)
  would be more convenient to script against.
