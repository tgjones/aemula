# Rework `Aemula.Benchmarks` as a real BenchmarkDotNet project

## Goal

Replace the hand-rolled stopwatch harness in `src/Aemula.Benchmarks` with a
genuine [BenchmarkDotNet](https://benchmarkdotnet.org) (BDN) project, keeping the
same project name and slot in `Aemula.slnx`. The point is fast, trustworthy
perf iteration on the emulation core: one benchmark per system, each running a
fixed workload of enough system-level `Tick()`s to exercise every periodic
codepath (video every N ticks, audio, timers, interrupts, frame-boundary
handling), plus the two cross-cutting scenarios the current harness already
found useful (debugger scaffolding overhead, NTSC decode pipeline).

## Why BDN over what we have now

The current `Program.cs` does its own warmup loop, its own `Stopwatch` batching,
and its own ns/cycle math. It works, but:

- **No statistics.** Single wall-clock numbers on a machine the perf memo
  (`aemula-perf-benchmarking`) records as having 10-30%+ run-to-run variance.
  Every real finding so far needed a manual "alternating A/B, 3-4 runs each"
  ritual to be believed. BDN does multi-invocation, multi-iteration, outlier
  removal, and reports mean + error + stddev by default.
- **No allocation tracking.** The single biggest win recorded in the memo (the
  `Array.Copy` / `Buffer.MemmoveInternal` per-sample cost in
  `NtscYiqDecoder.Process`) is exactly what `MemoryDiagnoser` surfaces for free.
- **Debug-build footgun.** The memo's #1 lesson is "a Debug-config run alone
  plausibly explains most of an observed slow-UI report." BDN refuses to run a
  non-optimized assembly unless explicitly forced, so this class of mistake
  becomes structurally impossible.
- **Awkward to filter/extend.** Adding a scenario means more bespoke plumbing.
  With BDN a new `[Benchmark]` method or a new class is the whole change, and
  `--filter '*Atari*'` / `--list` come for free.
- **Profiler integration.** `--profiler EP` emits an EventPipe trace that feeds
  straight into the existing `dotnet-trace` / speedscope workflow, scoped to a
  single benchmark instead of a whole process run.

What we keep from the current harness: the *three scenario shapes* (raw
`Tick()`, `Debugger.RunForDuration` production path, `Television.Decode` in
isolation + its four NTSC sub-stages) and the "% of real-time budget" readout
the user asked for, re-expressed as a BDN custom column.

## Systems in scope

From `EmulatedSystem` subclasses that are wired up today (same five the old
harness lists):

| System | `CyclesPerSecond` | Notes |
| --- | --- | --- |
| Apple II | 14,318,180 | master osc; feeds `Television.Decode` from `Tick()` |
| Atari 2600 | 3,580,000 | color clock; TIA + RIOT + composite video per tick |
| Chip-8 | 600 | no video-tick subdivision; timers divide by 10 |
| NES | 21,477,272 | master clock; inner `_ppuCycle` 0..12 loop, CPU on `_ppuCycle == 0` |
| Space Invaders | 19,968,000 | master clock; two IRQs/frame (mid-screen + VBLANK) |

Out of scope for now: `AcornSystem1System` (does not derive from
`EmulatedSystem`), `BbcMicroModelB` (commented out). Add them later when they're
real systems — the base class below makes that a ~10-line addition each.

## Workload ROMs

Each system benchmark needs a deterministic, checked-in workload so the numbers
are comparable across machines and over time. `Tick()` throughput is somewhat
input-dependent (instruction mix, whether the program actually draws), so the
workload must be fixed and must reach a steady state that touches video/audio,
not sit in a boot spin-loop.

| System | `LoadProgram` behaviour | Proposed workload |
| --- | --- | --- |
| Apple II | ignores arg; loads bundled `Apple2_Plus.rom` + `Apple2_Video.rom` from `AppContext.BaseDirectory` | none needed — boots to the Applesoft prompt (text video active). Warm past reset. |
| Space Invaders | ignores arg; loads bundled `invaders.[efgh]` | none needed — attract mode runs the full video + IRQ path. Warm past reset. |
| Chip-8 | reads file into RAM at `ProgramStart` | bundle `test_opcode.ch8` (already in `Aemula.Tests/Emulation/Systems/Chip8/Assets/`) — it draws to the display, exercising the sprite/XOR path. |
| NES | `Cartridge.FromFile(path)` | bundle `nestest.nes` (already in `Aemula.Tests/Emulation/Chips/Ricoh2A03/Assets/`); run in its automated mode so the PPU renders. |
| Atari 2600 | `File.ReadAllBytes(path)` → `Cartridge.FromData` | **decision needed** (see Open questions). Preferred: a tiny purpose-built 4K test kernel (`atari2600/bench.asm` + assembled `bench.bin`) that sets TIA colours, does `WSYNC` + playfield writes every scanline, and cycles sprite positions — deterministic, redistributable, exercises CPU + TIA + RIOT + composite video every line. |

Layout: `src/Aemula.Benchmarks/Workloads/<system>/<file>`, copied to output via a
`<Content>` glob in the csproj. Reuse the existing test ROMs by linking them
(`<Content Include="..\Aemula.Tests\Emulation\...\test_opcode.ch8" Link="Workloads\chip8\test_opcode.ch8">`)
rather than duplicating bytes, so there's one source of truth.

The bundled Apple II / Space Invaders / BBC ROMs already flow to
`Aemula.Benchmarks/bin/**/Emulation/Systems/**/Roms` transitively through the
`Aemula` project reference (`Aemula.csproj`'s `<Content Include="Emulation\**\Roms\*.*">`),
so those two systems need no workload plumbing.

## How many ticks per invocation

Each benchmark runs a fixed `TicksPerInvocation` count. It must be large enough
that **every periodic codepath fires many times** within one invocation:

- **Sub-frame periodicities.** Apple II / SI video shift every few dot clocks;
  Atari TIA composite video updates every color clock but playfield reflect /
  HMOVE / HBLANK gating vary within a scanline; NES runs one CPU cycle per 12
  master ticks. A single scanline covers the fastest of these; a handful of
  scanlines covers all of them.
- **Per-frame periodicities.** VSYNC/VBLANK/overscan regions, Space Invaders'
  two interrupts, the 60 Hz timer tick (Chip-8 divides its 600 Hz clock by 10;
  Atari RIOT timer; Apple II keyboard strobe decay). Need ≥1 full frame.
- **Frame-boundary handling.** NTSC odd/even field differences (Apple II's
  ~262.5 lines, NES's skipped dot on odd frames). Need ≥2 full frames so both
  fields are hit.

So the rule is **`TicksPerInvocation` = `Frames` × (`CyclesPerSecond` / 60)**,
with `Frames = 2` for the video systems, and a plain floor for Chip-8 (whose
"frame" is only 10 cycles):

| System | ticks / 60 Hz frame | `Frames` | `TicksPerInvocation` | approx wall time* |
| --- | --- | --- | --- | --- |
| Apple II | ~238,600 | 2 | ~477,000 | ~7 ms |
| Atari 2600 | ~59,700 | 2 | ~119,000 | ~2 ms |
| NES | ~357,950 | 2 | ~716,000 | ~8 ms |
| Space Invaders | ~332,800 | 2 | ~666,000 | ~8 ms |
| Chip-8 | 10 | — | 600,000 (flat; = 1 emulated second, 60 timer ticks) | ~1 ms |

\* rough, from the memo's ~15-70 ns/tick figures; only needs to clear BDN's
timer resolution comfortably, which all of these do.

`Frames` is a `const` on each benchmark class (not a `[Params]`) to keep the
result table one row per system. Optionally expose `[Params(1, 2, 4)] int Frames`
behind a compile-time or CLI opt-in if a sweep is ever wanted; default off.

## Benchmark taxonomy

### 1. Per-system tick throughput — one class per system

```
Benchmarks/
  SystemBenchmark.cs          // abstract base
  AppleIIBenchmark.cs
  Atari2600Benchmark.cs
  Chip8Benchmark.cs
  NesBenchmark.cs
  SpaceInvadersBenchmark.cs
```

`SystemBenchmark` (abstract):

- `protected abstract EmulatedSystem CreateSystem();`
- `protected abstract string? WorkloadPath { get; }` (null for Apple II / SI)
- `protected virtual int WarmupTicks => (int)(CyclesPerSecond / 60);` — run one
  frame in `[GlobalSetup]` after `LoadProgram` so measurement starts in steady
  state, past reset transients.
- `public abstract int TicksPerInvocation { get; }`
- `[GlobalSetup]` builds the system, `LoadProgram(WorkloadPath ?? "")`, warms it.
- `[Benchmark] public int Tick()` — `for (i in TicksPerInvocation) _system.Tick();`
  returns an `int` accumulator (e.g. XOR of `Television.CurrentRow` each Nth
  tick, or a framebuffer byte) so the JIT can't dead-code the loop. Returning
  the value is enough; BDN consumes `[Benchmark]` return values.

Each concrete class is tiny: override the two abstracts + `Frames`/`TicksPerInvocation`.

Determinism note: systems already seed their RNGs with constants
(`Chip8System` uses `new Random(42)`). Any residual nondeterminism in *which*
instructions execute is acceptable for a throughput benchmark as long as the
workload and warmup are fixed; we are measuring ns/tick, not output.

### 2. Debugger overhead — `DebuggerOverheadBenchmark`

Parameterized by system (`[ParamsSource]` over the systems whose
`CreateDebugger()` is non-null). Two `[Benchmark]`s over the same
`TicksPerInvocation` worth of work:

- `RawTick` — `[Benchmark(Baseline = true)]`, plain `system.Tick()` loop.
- `DebuggerRunForDuration` — `debugger.ActiveStepModeIndex = -1; debugger.Stopped = false;`
  then `debugger.RunForDuration(chunk)` in ~17 ms chunks, matching Aemula.UI's
  per-frame call. Count executed cycles via the `Ticked` event (it can run
  fewer than requested).

The `Ratio` column then reads directly as "how much the breakpoint /
step-mode / disassembler scaffolding costs on top of the system itself."

### 3. NTSC decode pipeline — `TelevisionDecodeBenchmark`

No system behind it — feed `Television.Decode` synthetic, signal-shaped samples
(slowly varying column/phase, `Random(0)` for amplitude), same as the current
harness. Benchmarks:

- `Decode` — `[Benchmark(Baseline = true)]`, full `Television.Decode(sample)`.
- `NtscSyncSeparator_Process`
- `NtscRasterOscillators_Process`
- `NtscColorBurstPll_Process`
- `NtscYiqDecoder_Process`

Keep the memo's caveat in a comment: once these `Process` bodies got small the
JIT inlined them into `Decode`, so the four sub-stage numbers do **not**
reliably sum to the whole, and `[MethodImpl(NoInlining)]` didn't restore
separate frames. The sub-stage benchmarks are a rough guide to relative cost,
not an additive decomposition — for a real attribution question use the
stub-one-stage-and-diff technique against scenario 1's Apple II benchmark.

## Config

A single `BenchmarkConfig : ManualConfig` (or attributes on a base class):

- `AddJob(Job.Default.WithRuntime(CoreRuntimeMoniker.Net10_0))` as the committed
  job. Add a **`short` job** (`Job.Default.WithIterationCount(3).WithWarmupCount(2)`,
  id `"short"`) selectable via `--job short` for ~seconds-per-benchmark
  iteration while chasing a change; full job for numbers worth recording.
- `AddDiagnoser(MemoryDiagnoser.Default)` — always on. Per-op allocations are a
  first-class signal for this codebase.
- `AddColumn(new RealtimeBudgetColumn())` — custom `IColumn` (see below).
- `AddExporter(MarkdownExporter.GitHub, JsonExporter.Full)` — GitHub markdown to
  paste into PRs/notes; full JSON so before/after runs can be diffed.
- `WithOptions(ConfigOptions.DisableLogFile)` optional to reduce noise.
- Leave the toolchain default (out-of-process, isolated). If BDN's generated
  build project fights central package management / `Directory.Build.props`,
  fall back to `InProcessEmitToolchain` on the job (documented trade-off: no
  process isolation, slightly noisier, but no generated project).

### `RealtimeBudgetColumn`

Reproduces the old "% of real-time budget" / "ms to simulate one 17 ms frame"
readout. For a benchmark case it needs `CyclesPerSecond` and
`TicksPerInvocation`; get them by instantiating the benchmark type (they're
cheap `get`-only members on `SystemBenchmark`) or from a static registry keyed
by type. Then:

```
nsPerTick        = report.Mean / ticksPerInvocation
nominalNsPerTick  = 1e9 / cyclesPerSecond
budgetPct         = nominalNsPerTick / nsPerTick          // >100% = keeps up with real time
msPerFrame        = (cyclesPerSecond / 60) * nsPerTick / 1e6
```

Show `budgetPct` and `msPerFrame` as two columns. Only populated for
`SystemBenchmark`-derived cases; blank for the television/debugger classes.
This is a nice-to-have — raw ns/op + `MemoryDiagnoser` already cover most
iteration needs, so it can land in a follow-up commit if the `IColumn` plumbing
is fiddly.

## File-by-file changes

**`src/Directory.Packages.props`** — add:
```xml
<PackageVersion Include="BenchmarkDotNet" Version="0.14.0" />   <!-- or latest stable -->
```
(One `restore` needs network access the first time.)

**`src/Aemula.Benchmarks/Aemula.Benchmarks.csproj`** — becomes:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Aemula\Aemula.csproj" />
    <PackageReference Include="BenchmarkDotNet" />
  </ItemGroup>
  <ItemGroup>
    <!-- reuse existing test ROMs, don't duplicate bytes -->
    <Content Include="..\Aemula.Tests\Emulation\Systems\Chip8\Assets\test_opcode.ch8"
             Link="Workloads\chip8\test_opcode.ch8" CopyToOutputDirectory="PreserveNewest" />
    <Content Include="..\Aemula.Tests\Emulation\Chips\Ricoh2A03\Assets\nestest.nes"
             Link="Workloads\nes\nestest.nes" CopyToOutputDirectory="PreserveNewest" />
    <Content Include="Workloads\atari2600\bench.bin" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```
`Directory.Build.props` already pins `net10.0` and the test-project block is
`IsTestProject`-gated, so nothing there needs touching.

**`src/Aemula.Benchmarks/Program.cs`** — replace entire body with:
```csharp
using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
```

**New files:**
- `Benchmarks/SystemBenchmark.cs` + five concrete classes
- `Benchmarks/DebuggerOverheadBenchmark.cs`
- `Benchmarks/TelevisionDecodeBenchmark.cs`
- `Benchmarks/BenchmarkConfig.cs`
- `Benchmarks/RealtimeBudgetColumn.cs` (optional / follow-up)
- `Workloads/atari2600/bench.asm` + `bench.bin` (see Open questions)
- short `README.md` in the project root with the commands below

**`.gitignore`** — already has `BenchmarkDotNet.Artifacts/` (line 50) and
`[Bb]in/` / `[Oo]bj/`. No change needed.

**`Aemula.slnx`** — project path and name unchanged, no edit.

**Memory** — update `aemula-perf-benchmarking.md`: the run command changes from
`dotnet run -c Release --project src/Aemula.Benchmarks -- appleii` to the
`--filter` form below; note the harness is now BDN with `MemoryDiagnoser` on by
default and a `short` job for iteration.

## Migration steps

1. Add `BenchmarkDotNet` to `Directory.Packages.props`; `dotnet restore`.
2. Rewrite the csproj and `Program.cs` as above.
3. Add `BenchmarkConfig` + `SystemBenchmark` base + the Apple II class only.
   `dotnet run -c Release --project src/Aemula.Benchmarks -- --filter '*AppleII*'`
   and confirm it runs, reports, and the budget number roughly matches the
   memo's last Apple II figure (~87% of budget on the full pipeline).
4. Add the other four system classes + their workloads. For Atari, resolve the
   kernel decision and check in `bench.asm` + `bench.bin`.
5. Port `DebuggerOverheadBenchmark` and `TelevisionDecodeBenchmark`.
6. Add `RealtimeBudgetColumn` (or defer).
7. Delete the old scenario code paths from `Program.cs` history — nothing else
   references them (`Aemula.Console/FrameRunner.cs` has its own copy of the
   "raw Tick loop" comment but no code dependency).
8. Update the memory note and the project `README.md`.
9. Run the full set once (`--filter '*'`, full job), paste the GitHub-markdown
   table into the PR description as the baseline.

## Workflow the rework enables

```bash
# everything, committed-quality numbers
dotnet run -c Release --project src/Aemula.Benchmarks -- --filter '*'

# one system, fast iteration loop while changing code
dotnet run -c Release --project src/Aemula.Benchmarks -- --filter '*Atari2600*' --job short

# see what's available
dotnet run -c Release --project src/Aemula.Benchmarks -- --list flat

# NTSC decode pipeline only
dotnet run -c Release --project src/Aemula.Benchmarks -- --filter '*TelevisionDecode*'

# capture an EventPipe trace for dotnet-trace / speedscope, one benchmark
dotnet run -c Release --project src/Aemula.Benchmarks -- --filter '*AppleII*' --profiler EP

# before/after a change: keep the JSON, diff the means
dotnet run -c Release --project src/Aemula.Benchmarks -- --filter '*AppleII*' --artifacts before
#   ...make the change...
dotnet run -c Release --project src/Aemula.Benchmarks -- --filter '*AppleII*' --artifacts after
```

BDN writes results to `BenchmarkDotNet.Artifacts/results/` (git-ignored) as
markdown + JSON. The A/B ritual from the memo still applies for marginal
changes, but BDN's built-in stats + `--job short` make it far cheaper.

## Open questions / decisions

1. **Atari 2600 workload.** Purpose-built 4K test kernel (preferred:
   redistributable, deterministic, always runnable in CI) vs. an
   `AEMULA_BENCH_ATARI_ROM` env-var / CLI override pointing at a local
   commercial ROM with the benchmark skipping itself when unset. Recommend the
   kernel; the override can be added on top later. Writing the kernel is
   ~40-60 lines of 6507 asm — needs an assembler step (dasm) or a hand-assembled
   `byte[]` embedded in the class.
2. **NES `nestest` mode.** `nestest.nes` needs its automated-mode entry
   (`PC = 0xC000`) and still mostly exercises the CPU; the PPU renders little.
   Acceptable for a CPU-throughput number; if we want the PPU render path
   covered, use a small homebrew NROM demo instead. Decision: start with
   `nestest`, revisit if the NES PPU becomes a perf target.
3. **`RealtimeBudgetColumn` now or later.** Land the core rework first; add the
   column in a follow-up if the `IColumn` API friction isn't trivial.
4. **BDN version.** Pin to the latest stable at implementation time (≥ 0.14,
   which handles central package management).

## Out of scope / follow-ups

- Acorn System 1 and BBC Micro benchmarks (systems not fully wired up yet).
- Chip-level micro-benchmarks (`Mos6502Chip.Tick`, `TiaChip`, etc.) — easy to
  add as more classes once the project exists, but not part of this rework.
- A CI job that runs `--job short` and fails on a regression threshold —
  possible later via `--filter` + JSON diff, but needs a stable runner.
