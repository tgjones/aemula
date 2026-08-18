using System;
using System.Collections.Generic;
using System.Linq;

namespace Aemula.UI.Oscilloscope;

/// <summary>
/// Records one fixed-depth ring buffer of samples per channel. <see cref="Sample"/>
/// is meant to be called once per tick (see <see cref="Debugging.Debugger.Ticked"/>);
/// since that only happens while the emulator is actually running, capture freezes
/// for free whenever the debugger is paused/stopped.
/// </summary>
public sealed class ScopeRecorder
{
    public const int DefaultCapacity = 131_072;

    private readonly ulong[][] _samples;

    public ScopeRecorder(IReadOnlyList<ScopeChannelNode> roots, int capacity = DefaultCapacity)
    {
        Channels = ScopeChannelNode.Flatten(roots).ToArray();
        Capacity = capacity;

        _samples = new ulong[Channels.Length][];
        for (var i = 0; i < Channels.Length; i++)
        {
            _samples[i] = new ulong[capacity];
        }
    }

    public ScopeChannel[] Channels { get; }

    public int Capacity { get; }

    /// <summary>
    /// Index of the next sample slot to be written - i.e. the oldest retained
    /// sample once the buffer has wrapped.
    /// </summary>
    private int WriteIndex { get; set; }

    /// <summary>
    /// Total number of samples ever recorded, which keeps counting past
    /// <see cref="Capacity"/> once the buffer has wrapped.
    /// </summary>
    public long TotalSamples { get; private set; }

    public ReadOnlySpan<ulong> GetChannelBuffer(int channelIndex) => _samples[channelIndex];

    public void Sample()
    {
        for (var i = 0; i < Channels.Length; i++)
        {
            _samples[i][WriteIndex] = Channels[i].Read();
        }

        WriteIndex = (WriteIndex + 1) % Capacity;
        TotalSamples++;
    }
}
