using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Aemula.Emulation.Output.Wav;

namespace Aemula.Emulation.Systems.AppleI.Cassette;

/// <summary>
/// The "tape" side of the cassette-out jack: samples the ACI's tape-out
/// flip-flop at a fixed audio rate while recording, accumulating a mono buffer
/// that <see cref="WriteWav"/> then saves.
/// </summary>
/// <remarks>
/// What a cassette recorder captures off the MIC jack is the flip-flop's
/// square wave essentially unfiltered, and the ACI read routine only cares
/// about edge timing, so this samples the level directly rather than
/// band-limiting it (the monitor-speaker path does its own band-limiting for
/// listening). At 48 kHz against a 1-2 kHz carrier, edge placement is captured
/// to well within a fortieth of a bit cell.
/// </remarks>
public sealed class CassetteRecorder
{
    public const int SampleRate = 48_000;

    private readonly double _ticksPerSample;
    private readonly List<float> _samples = [];

    private bool _recording;
    private bool _level;
    private long _tick;
    private double _nextSampleTick;

    /// <param name="phi2Rate">The rate <see cref="Tick"/> is called at.</param>
    public CassetteRecorder(double phi2Rate)
    {
        _ticksPerSample = phi2Rate / SampleRate;
    }

    public bool IsRecording => _recording;

    /// <summary>The tape-out flip-flop level, sampled by <see cref="Tick"/>.</summary>
    public bool Level
    {
        set => _level = value;
    }

    public IReadOnlyList<float> Samples => _samples;

    /// <summary>Begin a fresh recording, discarding anything captured before.</summary>
    public void Start()
    {
        _samples.Clear();
        _tick = 0;
        _nextSampleTick = 0.0;
        _recording = true;
    }

    /// <summary>Stop recording; the captured <see cref="Samples"/> are kept.</summary>
    public void Stop()
    {
        _recording = false;
    }

    /// <summary>One φ2 cycle: emit any output samples now due at the current level.</summary>
    public void Tick()
    {
        if (!_recording)
        {
            return;
        }

        _tick++;
        while (_tick >= _nextSampleTick)
        {
            _samples.Add(_level ? 1f : -1f);
            _nextSampleTick += _ticksPerSample;
        }
    }

    /// <summary>Write everything captured so far as a 16-bit mono WAV.</summary>
    public void WriteWav(string path)
    {
        using var stream = File.Create(path);
        WriteWav(stream);
    }

    /// <summary>Write everything captured so far as a 16-bit mono WAV.</summary>
    public void WriteWav(Stream stream)
    {
        WavWriter.Write(stream, CollectionsMarshal.AsSpan(_samples), SampleRate);
    }
}
