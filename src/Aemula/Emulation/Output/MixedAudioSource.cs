using System;
using System.Collections.Generic;

namespace Aemula.Emulation.Output;

// Sums several IAudioSources into one, for a Rig whose system and one or more
// peripherals each produce sound (a Famicom with the Disk System's audio plus a
// Data Recorder's tape monitor). The common case - one source, or none - never
// reaches here; the Rig hands that single source straight through.
//
// Each child has already resampled to the shared 48 kHz output rate, so mixing
// is a per-sample add; the sum is clamped to [-1, 1]. Drift trim and Reset fan
// out to every child.
public sealed class MixedAudioSource : IAudioSource
{
    private readonly IReadOnlyList<IAudioSource> _sources;
    private float[] _scratch = [];

    public MixedAudioSource(IReadOnlyList<IAudioSource> sources)
    {
        _sources = sources;
    }

    public float MasterVolume { get; set; } = 1f;

    // The most any single child can currently supply without underrunning; a
    // consumer sizing a read to this gets a fully-populated mix from at least
    // one source and silence-padding from the rest.
    public int AvailableOutputSamples
    {
        get
        {
            var min = int.MaxValue;
            foreach (var source in _sources)
            {
                min = Math.Min(min, source.AvailableOutputSamples);
            }

            return min == int.MaxValue ? 0 : min;
        }
    }

    public int Read(Span<float> destination)
    {
        destination.Clear();

        if (_scratch.Length < destination.Length)
        {
            _scratch = new float[destination.Length];
        }

        var scratch = _scratch.AsSpan(0, destination.Length);
        var produced = 0;

        foreach (var source in _sources)
        {
            scratch.Clear();
            produced = Math.Max(produced, source.Read(scratch));
            for (var i = 0; i < destination.Length; i++)
            {
                destination[i] += scratch[i];
            }
        }

        var volume = MasterVolume;
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = Math.Clamp(destination[i] * volume, -1f, 1f);
        }

        return produced;
    }

    public void SetResampleTrim(double trim)
    {
        foreach (var source in _sources)
        {
            source.SetResampleTrim(trim);
        }
    }

    public void Reset()
    {
        foreach (var source in _sources)
        {
            source.Reset();
        }
    }
}
