# AGENTS.md

## Build & test

```sh
cd src
dotnet build Aemula.slnx
```

Full suite (`dotnet test Aemula.Tests/Aemula.Tests.csproj`) takes **~40 minutes** -
Don't run it as a routine verification
step; run targeted tests for whatever you touched instead, and only run the
full suite if explicitly asked.

To run one test class:

```sh
dotnet test Aemula.Tests/Aemula.Tests.csproj --no-build --treenode-filter "/*/*/Ttl74157ChipTests/*"
```

The filter is a 4-segment path, `/Assembly/Namespace/Class/TestName`. Gotchas:

- Always use exactly 4 segments (`/*/*/ClassName/*`) regardless of how deeply
  nested the class's namespace is - a namespace like
  `Aemula.Tests.Emulation.Systems.Atari2600` is still one path segment, not
  four, so `/*/*/*/ClassName/*` (5 segments) won't match anything.
- `**` is only accepted as the *final* segment of the whole filter (e.g. to
  match everything below a point); using it in the middle, like
  `/*/**/ClassName/*`, throws.
- Wildcards work mid-segment too, so `/*/*Atari2600/*/*` runs every test in
  any namespace ending in `Atari2600`.
- A filter that matches nothing still reports `Zero tests ran` / `error: 1`
  with a non-zero exit code rather than passing silently - but it's easy to
  misread the noisy TUnit banner output as a pass, so check the summary line.

Benchmarks (`src/Aemula.Benchmarks`, BenchmarkDotNet, headless - no SDL/ImGui)
are separate from the UI and safe to run/iterate on freely:

```sh
dotnet run -c Release --project Aemula.Benchmarks -- --filter '<pattern>'
```

BDN refuses to run a non-Release build. See `Aemula.Benchmarks/README.md`.

For a headless visual check (no need to launch `Aemula.UI` itself), `Aemula.Console`
can run a system for N frames and dump a screenshot - see its `--help`-style
usage message in `Program.cs` for flags (`--screenshot`, `--screenshot-every`,
`--input`, etc).

## Plan docs

Working plans live in `docs/*-plan.md` or a system's own `docs/` folder and
get **deleted once the described work fully lands**. Never write a code or
test comment that cites a plan by name or phase number (e.g. "Phase 3", "per
the plan") - it'll dangle once the file is gone. Put the actual hardware fact
or rationale in the comment instead.

## Git

If asked to execute a multi-step task (e.g. work through several steps of a
plan in one go), commit between steps. If asked to execute a single step of a
plan, or to do an arbitrary task that isn't part of a plan, leave the result
uncommitted for review instead.