using System;

namespace Aemula.Emulation.Output;

// The IAudioSource every system without sound hands back. Having a real
// object here - rather than a null reference the consumer has to test for -
// is the whole point: the SDL audio callback, a WAV dump, or a test can
// pull from any system's audio source unconditionally, and a silent system
// simply produces silence and reports nothing buffered. Stateless, so one
// shared instance serves every such system at once.
public sealed class NullAudioSource : IAudioSource
{
    public static NullAudioSource Instance { get; } = new();

    private NullAudioSource()
    {
    }

    // Stored so a caller that sets it can read the same value back (a UI
    // that binds a volume slider to whatever the current system exposes),
    // but nothing here ever applies it - there is no signal to scale.
    public float MasterVolume { get; set; } = 1f;

    // Nothing is ever buffered, so nothing can ever be produced.
    public int AvailableOutputSamples => 0;

    // Always a full underrun: fill the span with silence and report zero
    // samples produced, matching AudioOutput's own underrun behaviour so a
    // consumer needs no special case for a silent system.
    public int Read(Span<float> destination)
    {
        destination.Clear();
        return 0;
    }

    // No resampler to trim.
    public void SetResampleTrim(double trim)
    {
    }

    // No state to clear.
    public void Reset()
    {
    }
}
