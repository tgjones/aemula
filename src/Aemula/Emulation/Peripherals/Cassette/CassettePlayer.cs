using System;

namespace Aemula.Emulation.Peripherals.Cassette;

/// <summary>
/// The "tape" side of the cassette-in jack: a buffer of mono audio (decoded
/// from a WAV file) that is played out one sample per φ2 CPU cycle into the
/// <see cref="Aemula.Emulation.Chips.Lm311Chip"/> comparator, resampling from
/// the file's rate to the CPU-cycle rate by linear interpolation.
/// </summary>
/// <remarks>
/// <para>
/// At ~1 MHz the CPU-cycle rate is hundreds of times the ACI's 1-2 kHz carrier,
/// so linear interpolation is far more than enough to preserve the zero-crossing
/// timing the comparator keys off. With no tape loaded (or once it has played
/// out) <see cref="NextSample"/> returns silence, which the comparator holds as
/// a steady level.
/// </para>
/// <para>
/// The reel only advances while <see cref="IsRunning"/> - the deck's PLAY state.
/// While stopped, <see cref="NextSample"/> keeps returning the level at the
/// current position without moving, so the comparator sees a steady line.
/// </para>
/// </remarks>
public sealed class CassettePlayer
{
    private readonly double _consumerRate;

    private float[] _samples = [];
    private int _sourceSampleRate;
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

    /// <summary>
    /// Whether the reel is turning (PLAY vs STOP). <see cref="NextSample"/> only
    /// advances the position while this is true. Set true by <see cref="Insert"/>
    /// so a freshly loaded tape is ready to roll; the Apple I deck stops it again
    /// straight afterwards so the operator presses PLAY themselves.
    /// </summary>
    public bool IsRunning { get; set; }

    /// <summary>Whether a tape is loaded (regardless of position or PLAY state).</summary>
    public bool HasTape => _samples.Length > 0;

    /// <summary>Whether a tape is loaded and has samples still to play.</summary>
    public bool IsPlaying => _cursor < _samples.Length;

    /// <summary>Play position in seconds, clamped to <see cref="LengthSeconds"/>.</summary>
    public double PositionSeconds =>
        _sourceSampleRate == 0 ? 0.0 : Math.Min(_cursor, _samples.Length) / _sourceSampleRate;

    /// <summary>Total tape length in seconds.</summary>
    public double LengthSeconds =>
        _sourceSampleRate == 0 ? 0.0 : (double)_samples.Length / _sourceSampleRate;

    /// <summary>Load a tape and rewind to its start. The reel starts running.</summary>
    public void Insert(float[] monoSamples, int sourceSampleRate)
    {
        _samples = monoSamples;
        _sourceSampleRate = sourceSampleRate;
        _step = sourceSampleRate / _consumerRate;
        _cursor = 0.0;
        IsRunning = true;
    }

    /// <summary>Remove the tape; <see cref="NextSample"/> then returns silence.</summary>
    public void Eject()
    {
        _samples = [];
        _sourceSampleRate = 0;
        _cursor = 0.0;
        IsRunning = false;
    }

    /// <summary>Rewind the loaded tape to its start.</summary>
    public void Rewind()
    {
        _cursor = 0.0;
    }

    /// <summary>
    /// The interpolated tape level for this φ2 cycle. Advances one cycle only
    /// while <see cref="IsRunning"/>; while stopped, returns the level at the
    /// current position without moving.
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

        if (IsRunning)
        {
            _cursor += _step;
        }

        return a + ((b - a) * frac);
    }
}
