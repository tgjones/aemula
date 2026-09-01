using Aemula.Emulation.Output;

namespace Aemula.Emulation.Systems.AppleII;

// The per-system audio hop for the Apple II. This is deliberately the opposite
// shape to Atari2600System.Audio.cs (and to Television): the 2600 puts a real,
// continuously-meaningful audio signal on a line into its RF modulator, so that
// file is a *receiver* - it samples TIA's audio line at the chip's true clock
// rate and hands the stream to AudioOutput to be DC-blocked, band-limited and
// resampled. The Apple II has no such signal. $C030 clocks a 74LS74 whose Q
// drives a transistor wired straight to a speaker cone soldered to the board;
// the "signal" is nothing more than the instants that pin flipped. Almost none
// of the 14 MHz timeline carries any audio information at all.
//
// So this models the *actuator*, not a receiver. Speaker is edge-driven: each
// time the flip-flop's Q changes, AppleIISystem hands Speaker the new pin level
// (see ToggleSpeaker in AppleIISystem.GameIO.cs) and Speaker splices a
// band-limited step into its 48 kHz output at the position its free-running
// tick counter is at right now. There is no periodic sampling of the pin and no
// AudioOutput filter stack - BLEP synthesis does the band-limiting at the point
// each edge goes in.
public sealed partial class AppleIISystem
{
    // 14_318_180 is the master clock - AppleIISystem.CyclesPerSecond, the rate
    // Tick() (and therefore _speaker.Tick(), see AppleIISystem.VideoTiming.cs)
    // is driven at. It is written as a literal rather than as CyclesPerSecond
    // only because CyclesPerSecond is a non-const instance property (an
    // override) and a field initializer may not read one; the two must stay in
    // step by hand.
    private readonly Speaker _speaker = new(14_318_180);

    // The field-backed override (see EmulatedSystem.Audio's remarks on why a
    // plain field, not a factory call reachable from the base constructor).
    public override IAudioSource Audio => _speaker;
}
