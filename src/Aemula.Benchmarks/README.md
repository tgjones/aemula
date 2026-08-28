# Aemula.Benchmarks

[BenchmarkDotNet](https://benchmarkdotnet.org) harness for the emulation core.
It references only `Aemula` (no `Aemula.UI`), so it builds and runs without
SDL/ImGui and is safe to iterate on freely.

## Benchmarks

| Class | What it measures |
| --- | --- |
| `AppleIIBenchmark`, `Atari2600Benchmark`, `Chip8Benchmark`, `SpaceInvadersBenchmark` | Raw `EmulatedSystem.Tick()` throughput for a fixed workload, `TicksPerInvocation` ticks per op (see `SystemSpecs.cs`). Enough ticks to exercise every periodic path: sub-frame video/audio dividers, per-frame sync regions, interrupts, the 60 Hz timer, the frame boundary. |
| `DebuggerOverheadBenchmark` | `Debugger.RunForDuration` (the Aemula.UI production path) vs. raw `Tick()`, same tick budget. `Ratio` = cost of the breakpoint / step-mode / disassembler scaffolding. Parameterised by system. |
| `TelevisionDecodeBenchmark` | `Television.Decode` (runs on every video sample regardless of whether TelevisionWindow is open) in isolation, plus each of its four NTSC stages. See the caveat in the class about inlining making the stage numbers non-additive. |

The custom **Realtime** column on the per-system benchmarks shows the mean as a
share of that system's real-time budget (>100% = faster than the real machine)
and the ms needed to simulate one 1/60 s frame.

## Workloads

- **Apple II / Space Invaders** boot their bundled ROMs (copied in via the
  `Aemula` project reference); no workload file needed.
- **Atari 2600** runs a purpose-built 4 KiB test kernel (`Atari2600TestKernel.cs`)
  that drives a full frame structure and rewrites the playfield / colour /
  sprite / HMOVE registers every scanline.
- **Chip-8** runs a small hand-written program (`Chip8TestProgram.cs`) that
  loops over sprite draw, the ALU group, RND, BCD, block load/store and timers.
- **NES** has no benchmark yet: `NesSystem`'s PPU/mapper support is still a stub,
  so there's no workload that stays on valid code for a whole invocation
  (nestest runs off the rails past its automated section). Add one once the NES
  is a real perf target.

## Running

Always `-c Release` - BenchmarkDotNet refuses to run a non-optimized build.

```bash
# everything, committed-quality numbers
dotnet run -c Release --project src/Aemula.Benchmarks -- --filter '*'

# one system, fast iteration loop (3 warmup + 3 measured iterations)
dotnet run -c Release --project src/Aemula.Benchmarks -- --filter '*Atari2600*' --job short

# even faster, just checks it runs (1 op, cold - not a real measurement)
dotnet run -c Release --project src/Aemula.Benchmarks -- --filter '*AppleII*' --job dry

# list what's available
dotnet run -c Release --project src/Aemula.Benchmarks -- --list flat

# NTSC decode pipeline only
dotnet run -c Release --project src/Aemula.Benchmarks -- --filter '*TelevisionDecode*'

# capture an EventPipe trace for one benchmark (feeds dotnet-trace / speedscope)
dotnet run -c Release --project src/Aemula.Benchmarks -- --filter '*AppleII*' --profiler EP

# before/after a change: keep the JSON/markdown, diff the means
dotnet run -c Release --project src/Aemula.Benchmarks -- --filter '*AppleII*' --artifacts before
#   ...make the change, rebuild...
dotnet run -c Release --project src/Aemula.Benchmarks -- --filter '*AppleII*' --artifacts after
```

Results are written to `BenchmarkDotNet.Artifacts/results/` (git-ignored) as
GitHub-flavoured markdown, CSV and HTML.

## Notes

- This machine has 10-30%+ run-to-run variance (see the `aemula-perf-benchmarking`
  memory note). For marginal changes still do same-session back-to-back A/B
  runs; `--job short` makes that cheap.
- `MemoryDiagnoser` is on for every benchmark - per-op allocations have been a
  first-class signal here.
- Adding a system: give it a `SystemSpec` in `SystemSpecs.cs` and a two-line
  `SystemBenchmark` subclass. Chip-level micro-benchmarks (`Mos6502Chip.Tick`
  etc.) can be added as plain BDN classes alongside.
