using Aemula.Emulation.Output;
using Aemula.Emulation.Output.Wav;
using Aemula.Emulation.Systems.AppleI.Cassette;

namespace Aemula.Emulation.Systems.AppleI;

// The optional Apple Cassette Interface card and its two audio jacks. The card
// itself (address decode, the tape-out flip-flop, the LM311 comparator, the
// firmware PROM) is AppleCassetteInterfaceCard, wired to the CPU bus through
// the generic IExpansionCard hook in AppleISystem.DoCpuMemoryAccess; this
// partial just owns the typed reference and the load/save entry points, and
// routes the tape-out signal to the host speakers.
public sealed partial class AppleISystem
{
    private readonly AppleCassetteInterfaceCard? _cassetteCard;

    // The Apple I has no sound of its own; when the cassette card is fitted,
    // its tape-out square wave is what there is to hear (the leader tone, the
    // data). Field-backed, per EmulatedSystem.Audio's remarks.
    public override IAudioSource Audio =>
        (IAudioSource?)_cassetteCard?.TapeOutSpeaker ?? NullAudioSource.Instance;

    /// <summary>Whether this machine was built with the cassette interface card.</summary>
    public bool HasCassetteInterface => _cassetteCard != null;

    /// <summary>Whether a recording is currently being captured from the tape-out jack.</summary>
    public bool IsRecordingCassette => _cassetteCard?.Recorder.IsRecording ?? false;

    private AppleCassetteInterfaceCard CassetteCardOrThrow =>
        _cassetteCard ?? throw new System.InvalidOperationException(
            "This Apple I was built without the cassette interface card.");

    /// <summary>Load a WAV file onto the cassette-in "tape".</summary>
    public void InsertCassette(string wavPath)
    {
        var wav = WavReader.Read(wavPath);
        CassetteCardOrThrow.Player.Insert(wav.Samples, wav.SampleRate);
    }

    /// <summary>Load already-decoded mono audio onto the cassette-in "tape".</summary>
    public void InsertCassette(float[] monoSamples, int sampleRate)
    {
        CassetteCardOrThrow.Player.Insert(monoSamples, sampleRate);
    }

    /// <summary>Remove the cassette-in "tape".</summary>
    public void EjectCassette()
    {
        _cassetteCard?.Player.Eject();
    }

    /// <summary>Start capturing the tape-out jack into a fresh recording.</summary>
    public void StartRecordingCassette()
    {
        CassetteCardOrThrow.Recorder.Start();
    }

    /// <summary>Stop capturing and write everything recorded so far to a WAV file.</summary>
    public void StopRecordingCassette(string wavPath)
    {
        var recorder = CassetteCardOrThrow.Recorder;
        recorder.Stop();
        recorder.WriteWav(wavPath);
    }
}
