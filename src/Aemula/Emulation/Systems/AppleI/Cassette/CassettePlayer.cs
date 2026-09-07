using System;

namespace Aemula.Emulation.Systems.AppleI.Cassette;

/// <summary>
/// The "tape" side of the cassette-in jack: a buffer of mono audio (decoded
/// from a WAV file) that is played out one sample per φ2 CPU cycle into the
/// <see cref="Aemula.Emulation.Chips.Lm311Chip"/> comparator, resampling from
/// the file's rate to the CPU-cycle rate by linear interpolation.
/// </summary>
/// <remarks>
/// At ~1 MHz the CPU-cycle rate is hundreds of times the ACI's 1-2 kHz carrier,
/// so linear interpolation is far more than enough to preserve the zero-crossing
/// timing the comparator keys off. With no tape loaded (or once it has played
/// out) <see cref="NextSample"/> returns silence, which the comparator holds as
/// a steady level.
/// </remarks>
public sealed class CassettePlayer
{
    private readonly double _consumerRate;

    private float[] _samples = [];
    private double _step;
    private double _cursor;

    /// <param name="consumerSampleRate">
    /// The rate <see cref="NextSample"/> is called at - the Apple I φ2 CPU-cycle
    /// rate (master clock / 14).
    /// </param>
    public CassettePlayer(double consumerSampleRate)
    {
        _consumerRate = consumerSampleRate;
    }

    /// <summary>Whether a tape is loaded and has samples still to play.</summary>
    public bool IsPlaying => _cursor < _samples.Length;

    /// <summary>Load a tape and rewind to its start.</summary>
    public void Insert(float[] monoSamples, int sourceSampleRate)
    {
        _samples = monoSamples;
        _step = sourceSampleRate / _consumerRate;
        _cursor = 0.0;
    }

    /// <summary>Remove the tape; <see cref="NextSample"/> then returns silence.</summary>
    public void Eject()
    {
        _samples = [];
        _cursor = 0.0;
    }

    /// <summary>Rewind the loaded tape to its start.</summary>
    public void Rewind()
    {
        _cursor = 0.0;
    }

    /// <summary>
    /// The interpolated tape level for this φ2 cycle, and advance one cycle.
    /// </summary>
    public float NextSample()
    {
        var index = (int)_cursor;
        if (index >= _samples.Length)
        {
            return 0f;
        }

        var frac = (float)(_cursor - index);
        var a = _samples[index];
        var b = index + 1 < _samples.Length ? _samples[index + 1] : a;
        _cursor += _step;

        return a + ((b - a) * frac);
    }
}
