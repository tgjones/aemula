using System;

namespace Aemula.UI.Oscilloscope;

/// <summary>
/// One recordable signal. Sampled once per tick as a <see cref="ulong"/> regardless
/// of <see cref="Kind"/>, so the recorder and ring buffer stay generic; <see cref="Kind"/>
/// and <see cref="BitWidth"/> only matter to rendering (step-trace vs. hex band).
/// <see cref="AnalogMin"/>/<see cref="AnalogMax"/>/<see cref="AnalogUnit"/> are
/// per-channel, since different Analog signals (composite video today, more later)
/// have different real-world ranges and units - <see cref="OscilloscopeWindow"/>
/// itself stays signal-agnostic, linearly scaling the raw 0-255 byte sample into
/// [AnalogMin, AnalogMax] for both the trace and the Y-axis, and generating the
/// two axis-endpoint labels (e.g. "0 V"/"2 V") from AnalogUnit itself.
/// </summary>
public sealed class ScopeChannel : ScopeChannelNode
{
    public ScopeChannelKind Kind { get; }

    public int BitWidth { get; }

    public Func<ulong> Read { get; }

    public double AnalogMin { get; }

    public double AnalogMax { get; }

    public string AnalogUnit { get; }

    private ScopeChannel(
        string name,
        ScopeChannelKind kind,
        int bitWidth,
        Func<ulong> read,
        double analogMin,
        double analogMax,
        string analogUnit)
        : base(name)
    {
        Kind = kind;
        BitWidth = bitWidth;
        Read = read;
        AnalogMin = analogMin;
        AnalogMax = analogMax;
        AnalogUnit = analogUnit;
    }

    public static ScopeChannel Digital(string name, Func<bool> read) =>
        new(name, ScopeChannelKind.Digital, bitWidth: 1, () => read() ? 1UL : 0UL, 0, 0, "");

    public static ScopeChannel Bus(string name, int bitWidth, Func<ulong> read) =>
        new(name, ScopeChannelKind.Bus, bitWidth, read, 0, 0, "");

    // BitWidth is unused for Analog (rendered as a continuous line, not a
    // hex band), but a byte sample still fits the shared ulong storage
    // unchanged - see docs/apple-ii-ntsc-video-plan.md's phase 5. The raw
    // sample is always a 0-255 byte (Read's own range); min/max are this
    // channel's own real-world range that byte gets linearly scaled into,
    // and unit is appended to the two labels OscilloscopeWindow generates
    // at the axis endpoints (e.g. "0 V"/"2 V") - every Analog channel
    // supplies its own rather than the window assuming one.
    public static ScopeChannel Analog(string name, Func<byte> read, double min, double max, string unit) =>
        new(name, ScopeChannelKind.Analog, bitWidth: 8, () => read(), min, max, unit);
}
