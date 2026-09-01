using System;

namespace Aemula.Emulation.Output;

// The audio-side counterpart to how the UI already consumes video: the
// rendering/playback layer never talks to a specific system's sound chip,
// it talks to whatever IAudioSource that system exposes. A system with real
// sound wires up an AudioOutput; a system without sound hands back
// NullAudioSource.Instance. The consumer (the SDL audio callback, a WAV
// dump, a test harness) then treats every system identically - it only ever
// pulls resampled 48 kHz mono float samples out of Read and adjusts the
// drift trim - so there is no "does this system have audio?" branching
// anywhere above this interface.
public interface IAudioSource
{
    // Linear gain applied to everything Read hands back, nominal range
    // [0, 1] but not clamped here (a consumer is free to push it past 1 and
    // accept the clipping). This is a playback-side convenience - a volume
    // slider, a mute - and is deliberately not part of the DSP chain, so
    // toggling it never disturbs filter state or timing. An implementation
    // with nothing to play (NullAudioSource) may store and return a value
    // here but is not required to honour it.
    float MasterVolume { get; set; }

    // A cheap estimate, made without producing anything, of how many
    // OutputSampleRate samples Read could return right now from the audio
    // already buffered. It is what a consumer's buffer-depth feedback loop
    // measures each frame to decide which way to nudge SetResampleTrim, and
    // what it uses to size its Read request. Only an estimate: the exact
    // count Read yields can differ by a sample or two because of where the
    // fractional resampler's read cursor currently sits.
    int AvailableOutputSamples { get; }

    // Fills up to destination.Length samples of resampled OutputSampleRate
    // (48 kHz) mono float audio, nominally in [-1, 1], and returns the
    // count actually written. A short return means underrun - the source
    // had less buffered audio than was asked for; the consumer treats the
    // unwritten tail as silence (an implementation may additionally zero it,
    // and both AudioOutput and NullAudioSource do). Called once per rendered
    // frame, on the same thread that drives the emulation tick loop that
    // feeds the source, so it must not block.
    int Read(Span<float> destination);

    // Drift trim from the consumer's buffer-depth feedback loop. The
    // emulator's real sample-production rate and the audio device's real
    // consumption rate are never exactly equal and drift apart over minutes;
    // the consumer watches its own buffer depth and calls this with a tiny
    // correction to hold it steady. Positive trim multiplies the effective
    // output rate by (1 + trim), i.e. asks for proportionally more output
    // samples per second of emulated input (used when the consumer's buffer
    // is draining); negative trim asks for fewer. |trim| stays tiny -
    // roughly < 0.02 - and an implementation may clamp it; anything larger
    // is audible as pitch error and means the feedback loop, not this call,
    // is misbehaving. Takes effect on subsequent Read calls.
    void SetResampleTrim(double trim);

    // Drops all buffered audio and returns every internal filter and
    // resampler to its power-on state, without changing MasterVolume or the
    // resample trim (those are consumer-owned playback settings, not signal
    // state). Used on a hard machine reset or a load-state, where carrying
    // stale samples across the discontinuity would produce an audible pop.
    void Reset();
}
