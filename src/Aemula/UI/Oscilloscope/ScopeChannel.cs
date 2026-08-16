using System;

namespace Aemula.UI.Oscilloscope;

/// <summary>
/// One recordable signal. Sampled once per tick as a <see cref="ulong"/> regardless
/// of <see cref="Kind"/>, so the recorder and ring buffer stay generic; <see cref="Kind"/>
/// and <see cref="BitWidth"/> only matter to rendering (step-trace vs. hex band).
/// </summary>
public sealed class ScopeChannel(string name, ScopeChannelKind kind, int bitWidth, Func<ulong> read)
    : ScopeChannelNode(name)
{
    public ScopeChannelKind Kind { get; } = kind;

    public int BitWidth { get; } = bitWidth;

    public Func<ulong> Read { get; } = read;

    public static ScopeChannel Digital(string name, Func<bool> read) =>
        new(name, ScopeChannelKind.Digital, bitWidth: 1, () => read() ? 1UL : 0UL);

    public static ScopeChannel Bus(string name, int bitWidth, Func<ulong> read) =>
        new(name, ScopeChannelKind.Bus, bitWidth, read);
}
