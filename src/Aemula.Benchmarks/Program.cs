using Aemula.Benchmarks;
using BenchmarkDotNet.Running;

// `profile <system> [seconds]` runs a flat Tick() loop in this process for a
// sampling profiler to wrap (see ProfileHarness). Everything else is BDN.
if (args.Length > 0 && args[0] == "profile")
{
    return ProfileHarness.Run(args);
}

// Every benchmark type in the assembly is exposed through BenchmarkSwitcher, so
// this is driven entirely by command-line args:
//
//   dotnet run -c Release --project src/Aemula.Benchmarks -- --filter '*'
//   dotnet run -c Release --project src/Aemula.Benchmarks -- --filter '*Atari2600*' --job short
//   dotnet run -c Release --project src/Aemula.Benchmarks -- --list flat
//
// See README.md in this directory for the full set of recipes.
BenchmarkSwitcher.FromAssembly(typeof(BenchmarkConfig).Assembly).Run(args, BenchmarkConfig.Create());
return 0;
