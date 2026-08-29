using System;
using Aemula.Emulation.Output;
using Aemula.Emulation.Output.Ntsc;
using BenchmarkDotNet.Attributes;

namespace Aemula.Benchmarks;

// Television.Decode runs on every emulated video sample, unconditionally,
// whether or not TelevisionWindow is open (see Television.Decode). This
// isolates that NTSC decode pipeline from any CPU/system behind it, fed
// signal-shaped synthetic samples, then breaks it into its four stages.
//
// Caveat carried over from the perf investigation (see the aemula-perf-benchmarking
// memory note): once these Process bodies got small the JIT inlined them into
// Decode, and [MethodImpl(NoInlining)] did not reliably restore separate
// frames. The four stage numbers are a rough guide to relative cost, NOT an
// additive decomposition of Decode. For a real attribution question, stub one
// stage at a time and diff the Apple II SystemBenchmark result.
public class TelevisionDecodeBenchmark
{
    private const int SampleCount = 4096; // power of two -> cheap wrap
    private const float BlackLevel = 40f;
    private const float WhiteLevel = 200f;

    private readonly byte[] _samples = new byte[SampleCount];
    private int _i;

    private Television _television = null!;
    private NtscSyncSeparator _syncSeparator = null!;
    private NtscRasterOscillators _rasterOscillators = null!;
    private NtscColorBurstPll _colorBurstPll = null!;
    private NtscYiqDecoder _yiqDecoder = null!;

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(0);
        random.NextBytes(_samples);

        _television = new Television();
        _syncSeparator = new NtscSyncSeparator();
        _rasterOscillators = new NtscRasterOscillators();
        _colorBurstPll = new NtscColorBurstPll();
        _yiqDecoder = new NtscYiqDecoder();
    }

    private byte NextSample() => _samples[_i++ & (SampleCount - 1)];

    [Benchmark(Baseline = true)]
    public int Decode()
    {
        _television.Decode(NextSample());
        return _television.CurrentRow;
    }

    [Benchmark]
    public void SyncSeparator_Process()
    {
        _syncSeparator.Process(NextSample());
    }

    [Benchmark]
    public void RasterOscillators_Process()
    {
        // Alternate hSync so the branch-heavy body isn't measured on one path.
        _rasterOscillators.Process((_i++ & 1) == 0, false);
    }

    [Benchmark]
    public void ColorBurstPll_Process()
    {
        var column = _i & 1023; // 0..1023, spans a scanline's worth of phase
        _colorBurstPll.Process(NextSample(), column, BlackLevel, WhiteLevel);
    }

    [Benchmark]
    public void YiqDecoder_Process()
    {
        var phase = (_i & 63) * (MathF.PI / 32f); // 0..2π in 64 steps
        _yiqDecoder.Process(NextSample(), phase, BlackLevel, WhiteLevel, colorBurstDetected: true);
    }
}
