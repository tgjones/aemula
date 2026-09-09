using System;
using System.Collections.Generic;
using System.IO;
using Aemula.Emulation.Output;
using Aemula.Emulation.Output.Wav;
using Aemula.Emulation.Systems;

namespace Aemula.Emulation.Peripherals.Cassette;

/// <summary>
/// A generic consumer cassette recorder, the kind you would patch into a
/// machine's cassette interface with two mono audio leads: one from the deck's
/// earphone jack into the interface (playback), one from the interface into the
/// deck's microphone jack (record). It models the transport - motor, counter,
/// the tape itself - and the heads; squaring the wobbly tape audio up into a
/// logic level is the machine-side interface card's job (the Apple I's ACI).
/// </summary>
/// <remarks>
/// The interface samples the playback lead once per one of its own cycles (the
/// Apple I ACI does it every φ2), so the deck is handed that sampling rate at
/// construction and its <see cref="CassettePlayer"/> / <see cref="CassetteRecorder"/>
/// resample between it and the WAV file's rate. Nothing ticks the deck on a
/// clock of its own - <see cref="ReadPlayback"/> and <see cref="WriteCapture"/>
/// are pulled by the interface.
/// </remarks>
public sealed class CassetteDeck : IPeripheral
{
    /// <summary>
    /// Which transport button is down. One motor drives the single capstan, so
    /// the states are mutually exclusive - you cannot play and record at once.
    /// </summary>
    public enum TransportMode
    {
        /// <summary>Reel stopped. Neither lead is live; the monitor is silent.</summary>
        Stopped,

        /// <summary>PLAY: the reel feeds the tape into the playback lead, and the monitor follows it.</summary>
        Playing,

        /// <summary>RECORD: the interface's tape-out lead is captured to the buffer, and the monitor follows it.</summary>
        Recording,
    }

    private readonly CassettePlayer _player;
    private readonly CassetteRecorder _recorder;

    // The deck's monitor speaker on the host output. It follows whichever head
    // is live: the interface's tape-out signal on its way to tape while
    // RECORDing, the tape coming off the head while PLAYing, silence while
    // STOPped. The Apple I itself is silent, so what this actually carries is
    // the leader tone and FSK data heard during a save or a load.
    private readonly Speaker _monitor;

    private readonly ConsoleControl[] _controls;

    private TransportMode _mode;

    // The playback lead's level from this interface cycle's ReadPlayback, kept
    // so WriteCapture (called later in the same cycle) can feed it to the
    // monitor while playing.
    private float _playbackLevel;

    /// <param name="interfaceSampleRate">
    /// The rate the connected interface card samples the playback lead at and
    /// pushes the capture lead at - the Apple I φ2 CPU-cycle rate (master clock
    /// / 14).
    /// </param>
    public CassetteDeck(double interfaceSampleRate)
    {
        _player = new CassettePlayer(interfaceSampleRate);
        _recorder = new CassetteRecorder(interfaceSampleRate);
        _monitor = new Speaker(interfaceSampleRate);

        // There is no motor control from the machine, so the transport is
        // worked by hand here the way you would work the buttons on the deck.
        _controls =
        [
            new ConsoleControl(
                "Rewind",
                "tape-rewind",
                ConsoleControl.ControlKind.Momentary,
                static () => false,
                pressed => { if (pressed) _player.Rewind(); }),

            new ConsoleControl(
                "Tape",
                "tape-play",
                ConsoleControl.ControlKind.Latching,
                () => _mode == TransportMode.Playing,
                playing => Mode = playing ? TransportMode.Playing : TransportMode.Stopped,
                offLabel: "Play",
                onLabel: "Stop"),

            ConsoleControl.CreateReadout(
                "Position",
                "tape-position",
                FormatPosition),
        ];
    }

    public string Name => "Cassette";

    public IReadOnlyList<ConsoleControl> Controls => _controls;

    public IAudioSource? Audio => _monitor;

    /// <summary>
    /// Which transport button is down. Setting it runs or stops the reel and
    /// the capture buffer to match: switching to <see cref="TransportMode.Recording"/>
    /// begins a fresh recording (discarding the previous one), and leaving it
    /// stops the current one with its samples kept. Idempotent - setting the
    /// mode it is already in does nothing.
    /// </summary>
    public TransportMode Mode
    {
        get => _mode;
        set
        {
            var wasRecording = _mode == TransportMode.Recording;
            _mode = value;

            _player.IsRunning = value == TransportMode.Playing;

            if (value == TransportMode.Recording)
            {
                if (!wasRecording)
                {
                    _recorder.Start();
                }
            }
            else if (_recorder.IsRecording)
            {
                _recorder.Stop();
            }
        }
    }

    // --- audio leads, pulled by the interface card once per interface cycle ---

    /// <summary>
    /// The playback lead: the interpolated tape level for this interface cycle.
    /// Advances the tape only while the reel is running (PLAY); otherwise holds
    /// the level at the current position.
    /// </summary>
    public float ReadPlayback()
    {
        _playbackLevel = _player.NextSample();
        return _playbackLevel;
    }

    /// <summary>
    /// The record lead: the interface card's tape-out level for this cycle,
    /// pushed once per interface cycle. Captured to the recording buffer while
    /// in <see cref="TransportMode.Recording"/>; also drives the monitor
    /// speaker, which follows whichever head is live - tape-out while recording,
    /// the playback lead while playing, silence while stopped.
    /// </summary>
    public void WriteCapture(float level)
    {
        var tapeOutHigh = level > 0f;

        if (_mode == TransportMode.Recording)
        {
            _recorder.Level = tapeOutHigh;
            _recorder.Tick();
        }

        _monitor.Level = _mode switch
        {
            TransportMode.Playing => _playbackLevel > 0f,
            TransportMode.Recording => tapeOutHigh,
            _ => false,
        };
        _monitor.Tick();
    }

    // --- transport ---

    /// <summary>Press PLAY (see <see cref="Mode"/>).</summary>
    public void Play() => Mode = TransportMode.Playing;

    /// <summary>Release the transport to STOP (see <see cref="Mode"/>).</summary>
    public void Stop() => Mode = TransportMode.Stopped;

    /// <summary>Rewind the loaded tape to its start.</summary>
    public void Rewind() => _player.Rewind();

    /// <summary>Whether the reel is turning (<see cref="TransportMode.Playing"/>).</summary>
    public bool IsRunning => _player.IsRunning;

    /// <summary>Whether a recording is currently being captured (<see cref="TransportMode.Recording"/>).</summary>
    public bool IsRecording => _recorder.IsRecording;

    /// <summary>The samples captured so far in the current/last recording.</summary>
    public IReadOnlyList<float> RecordedSamples => _recorder.Samples;

    /// <summary>Play position and total length of the loaded tape, in seconds.</summary>
    public (double Position, double Length) Counter =>
        (_player.PositionSeconds, _player.LengthSeconds);

    // --- media ---

    /// <summary>Whether a tape is loaded.</summary>
    public bool HasTape => _player.HasTape;

    /// <summary>
    /// Load a WAV file as the tape. Like putting a cassette in the deck, it goes
    /// in stopped and rewound - press <see cref="Play"/> when ready.
    /// </summary>
    public void InsertTape(string wavPath)
    {
        var wav = WavReader.Read(wavPath);
        InsertTape(wav.Samples, wav.SampleRate);
    }

    /// <summary>Load already-decoded mono audio as the tape (stopped, rewound).</summary>
    public void InsertTape(float[] monoSamples, int sampleRate)
    {
        _player.Insert(monoSamples, sampleRate);
        Mode = TransportMode.Stopped;
    }

    /// <summary>Remove the tape.</summary>
    public void EjectTape()
    {
        _player.Eject();
        Mode = TransportMode.Stopped;
    }

    /// <summary>Press RECORD: begin a fresh recording of the record lead (see <see cref="Mode"/>).</summary>
    public void StartRecording() => Mode = TransportMode.Recording;

    /// <summary>Release RECORD; the samples so far are kept.</summary>
    public void StopRecording()
    {
        if (_mode == TransportMode.Recording)
        {
            Mode = TransportMode.Stopped;
        }
    }

    /// <summary>Stop capturing and write everything recorded so far to a WAV file.</summary>
    public void SaveRecording(string wavPath)
    {
        StopRecording();
        _recorder.WriteWav(wavPath);
    }

    /// <summary>Stop capturing and write everything recorded so far as a WAV to a stream.</summary>
    public void SaveRecording(Stream stream)
    {
        StopRecording();
        _recorder.WriteWav(stream);
    }

    /// <summary>Accepts a <c>.wav</c> path as a tape (see <see cref="IPeripheral.TryLoadMedia"/>).</summary>
    public bool TryLoadMedia(string filePath)
    {
        if (!filePath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        InsertTape(filePath);
        return true;
    }

    public void Reset()
    {
        // A machine reset doesn't reach across the audio leads to the deck - the
        // tape keeps sitting where it is and a running reel keeps turning, like
        // real life. Only the monitor speaker is cleared, so buffered square
        // wave doesn't cross the discontinuity as a pop.
        _monitor.Reset();
    }

    public void Dispose()
    {
    }

    private string FormatPosition()
    {
        if (!_player.HasTape)
        {
            return "No tape";
        }

        return $"{FormatSeconds(_player.PositionSeconds)} / {FormatSeconds(_player.LengthSeconds)}";
    }

    private static string FormatSeconds(double seconds)
    {
        var whole = Math.Max(0, (int)Math.Floor(seconds));
        return FormattableString.Invariant($"{whole / 60}:{whole % 60:00}");
    }
}
