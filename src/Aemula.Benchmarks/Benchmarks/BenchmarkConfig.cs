using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;

namespace Aemula.Benchmarks;

internal static class BenchmarkConfig
{
    public static IConfig Create() =>
        ManualConfig.Create(DefaultConfig.Instance)
            .AddDiagnoser(MemoryDiagnoser.Default)
            .AddColumn(new RealtimeBudgetColumn());
}
