using System;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace Aemula.Benchmarks;

// Given the measured mean time
// for one invocation (= TicksPerInvocation Tick() calls), how does the achieved
// ns/tick compare to the system's real-time budget, and how long does it take
// to simulate one 1/60 s frame. Only populated for SystemBenchmark-derived
// cases; blank for the Television / debugger benchmarks.
internal sealed class RealtimeBudgetColumn : IColumn
{
    public string Id => nameof(RealtimeBudgetColumn);
    public string ColumnName => "Realtime";
    public string Legend => "Share of the system's real-time budget the mean achieves (>100% = faster than the real machine), and ms to simulate one 1/60 s frame.";
    public bool AlwaysShow => true;
    public ColumnCategory Category => ColumnCategory.Custom;
    public int PriorityInCategory => 0;
    public bool IsNumeric => false;
    public UnitType UnitType => UnitType.Dimensionless;

    public bool IsAvailable(Summary summary) => true;
    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase) => GetValue(summary, benchmarkCase, summary.Style);

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
    {
        var type = benchmarkCase.Descriptor.Type;
        if (!typeof(SystemBenchmark).IsAssignableFrom(type) || type.IsAbstract)
        {
            return "-";
        }

        var meanNs = summary[benchmarkCase]?.ResultStatistics?.Mean;
        if (meanNs is not > 0)
        {
            return "-";
        }

        SystemBenchmark instance;
        try
        {
            instance = (SystemBenchmark)Activator.CreateInstance(type)!;
        }
        catch
        {
            return "-";
        }

        var ticks = instance.TicksPerInvocation;
        var hz = instance.NominalCyclesPerSecond;
        if (ticks <= 0 || hz == 0)
        {
            return "-";
        }

        var nsPerTick = meanNs.Value / ticks;
        var nominalNsPerTick = 1_000_000_000.0 / hz;
        var ratio = nominalNsPerTick / nsPerTick; // >1 => faster than the real machine

        // Systems with a very low clock (Chip-8 at 600 Hz) run thousands of
        // times faster than real time and have no meaningful 1/60 s frame, so
        // the "% of budget / ms per frame" framing stops being useful - show a
        // plain speed multiple instead.
        if (ratio >= 20.0)
        {
            return $"{ratio:N0}x realtime";
        }

        var msPerFrame = hz / 60.0 * nsPerTick / 1_000_000.0;
        return $"{ratio * 100.0:N0}% ({msPerFrame:N2} ms/f)";
    }
}
