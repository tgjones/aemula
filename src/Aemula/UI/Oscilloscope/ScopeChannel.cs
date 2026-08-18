using System;
using System.Collections.Generic;

namespace Aemula.UI.Oscilloscope;

/// <summary>
/// One recordable signal. Sampled once per tick as a <see cref="ulong"/> regardless
/// of <see cref="Kind"/>, so the recorder and ring buffer stay generic; <see cref="Kind"/>
/// and <see cref="BitWidth"/> only matter to rendering (step-trace vs. hex band).
/// <see cref="AnalogMin"/>/<see cref="AnalogMax"/>/<see cref="AnalogTicks"/> are
/// per-channel, since different Analog signals (composite video today, more later)
/// have different meaningful ranges and anchor points - <see cref="OscilloscopeWindow"/>
/// itself stays signal-agnostic and just renders whatever a channel supplies.
/// </summary>
public sealed class ScopeChannel : ScopeChannelNode
{
    public ScopeChannelKind Kind { get; }

    public int BitWidth { get; }

    public Func<ulong> Read { get; }

    public double AnalogMin { get; }

    public double AnalogMax { get; }

    public IReadOnlyList<(double Value, string Label)> AnalogTicks { get; }

    private ScopeChannel(
        string name,
        ScopeChannelKind kind,
        int bitWidth,
        Func<ulong> read,
        double analogMin,
        double analogMax,
        IReadOnlyList<(double, string)> analogTicks)
        : base(name)
    {
        Kind = kind;
        BitWidth = bitWidth;
        Read = read;
        AnalogMin = analogMin;
        AnalogMax = analogMax;
        AnalogTicks = analogTicks;
    }

    public static ScopeChannel Digital(string name, Func<bool> read) =>
        new(name, ScopeChannelKind.Digital, bitWidth: 1, () => read() ? 1UL : 0UL, 0, 0, []);

    public static ScopeChannel Bus(string name, int bitWidth, Func<ulong> read) =>
        new(name, ScopeChannelKind.Bus, bitWidth, read, 0, 0, []);

    // BitWidth is unused for Analog (rendered as a continuous line, not a
    // hex band), but a byte sample still fits the shared ulong storage
    // unchanged - see docs/apple-ii-ntsc-video-plan.md's phase 5. min/max
    // and ticks are this channel's own Y-axis range and labeled anchor
    // points (e.g. composite video's Sync/Black/White byte levels) - every
    // Analog channel supplies its own rather than the window assuming one.
    public static ScopeChannel Analog(string name, Func<byte> read, double min, double max, IReadOnlyList<(double Value, string Label)> ticks) =>
        new(name, ScopeChannelKind.Analog, bitWidth: 8, () => read(), min, max, ticks);
}
