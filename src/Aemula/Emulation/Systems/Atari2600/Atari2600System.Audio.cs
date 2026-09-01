using Aemula.Emulation.Output;

namespace Aemula.Emulation.Systems.Atari2600;

// The receiver-side model of the real, continuously-meaningful audio signal
// that leaves the TIA - the audio counterpart of the composite-video summing
// in Atari2600System.CompositeVideo.cs, and structured the same way. Real TIA
// sums its two independent tone channels onto the single audio line that
// feeds the RF modulator; this reproduces that sum, one value per TIA audio
// clock, and pushes it into AudioOutput, which DC-blocks it, band-limits it
// and resamples it to the fixed 48 kHz the playback layer wants. Nothing
// above the IAudioSource this exposes needs to know TIA's odd ~31 kHz native
// rate, exactly as nothing above Television needs to know the 2600's raster
// geometry.
//
// Like the sibling file's CompositeVideoSampled wiring, this is driven purely
// by an event off the chip (TiaChip.AudioClocked) - it samples when TIA's
// real audio clock ticks, twice per scanline, rather than being polled from
// Atari2600System.Tick(). That keeps the summing stage locked to the true
// hardware cadence with no cross-tick phase state of its own.
public sealed partial class Atari2600System
{
    // TIA clocks its audio twice every 228-OSC scanline (Andrew Towers'
    // TIA_HW_Notes), so the mean interval between audio ticks is 228 / 2 =
    // 114 OSC cycles. CyclesPerSecond is the 3.58 MHz OSC rate, so the input
    // rate handed to AudioOutput is ~31,403.5 Hz. Constructed in the main
    // constructor rather than with a field initializer here: CyclesPerSecond
    // is an instance property (an override, so it cannot be const), and a
    // field initializer may not read one.
    private readonly AudioOutput _audio;

    // The field-backed override (see EmulatedSystem.Audio's remarks on why a
    // plain field, not a factory call reachable from the base constructor).
    public override IAudioSource Audio => _audio;

    // The TiaChip.AudioClocked handler - runs once per real TIA audio clock.
    private void WriteAudioSample()
    {
        // TIA's two channels are summed onto one pin. Each Sample is 0..15
        // (AUDV when the waveform bit is high, else 0), so the sum is 0..30;
        // /30f lands a two-channels-at-full-volume peak at 1.0, comfortably
        // inside AudioOutput's nominal [-1, 1]. The steady 0.5-ish DC offset a
        // duty-cycled square wave carries is removed by AudioOutput's own DC
        // blocker (its WriteSample front door runs it first) - no need to
        // centre it here.
        _audio.WriteSample((_tia.Audio0Sample + _tia.Audio1Sample) / 30f);
    }
}
