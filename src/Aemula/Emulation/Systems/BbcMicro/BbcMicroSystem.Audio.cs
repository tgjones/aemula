using Aemula.Emulation.Chips;
using Aemula.Emulation.Output;

namespace Aemula.Emulation.Systems.BbcMicro;

// IC18, the SN76489, is clocked by the video ULA's 4MHz output and written
// through System VIA port A, with the addressable latch's Q0 as its /WE. Its
// output goes through an analogue mixer and the LM386 amplifier, reduced here
// to the chip's own mix.
public sealed partial class BbcMicroSystem
{
    // The chip steps at 4MHz / 16 = 250kHz. Eight steps are averaged into
    // each sample handed to AudioOutput, which is a 31.25kHz stream, so that
    // the square waves' harmonics above that are filtered rather than folded
    // back into the audible band.
    private const int StepsPerAudioSample = 8;
    private const double AudioSampleRate = 4_000_000.0 / 16 / StepsPerAudioSample;

    // IC18.
    internal readonly Sn76489Chip Sound = new() { CeBar = false };

    private readonly AudioOutput _audio = new(AudioSampleRate);

    public override IAudioSource Audio => _audio;

    private bool _soundClock;
    private bool _soundWeBar = true;
    private uint _soundSteps;
    private float _soundAccumulator;

    private void TickSound()
    {
        var weBar = _ic32.Q0;
        if (weBar != _soundWeBar)
        {
            _soundWeBar = weBar;
            Sound.Data = SystemVia.PortA;
            Sound.WeBar = weBar;
        }

        var clock = VideoUla.Clock4MHz;
        if (clock == _soundClock)
        {
            return;
        }

        _soundClock = clock;
        Sound.Clock = clock;

        if (Sound.Steps == _soundSteps)
        {
            return;
        }

        _soundSteps = Sound.Steps;
        _soundAccumulator += Sound.Output;

        if (_soundSteps % StepsPerAudioSample == 0)
        {
            _audio.WriteSample(_soundAccumulator / (StepsPerAudioSample * 4));
            _soundAccumulator = 0;
        }
    }
}
