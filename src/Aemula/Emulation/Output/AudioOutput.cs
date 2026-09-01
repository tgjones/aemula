using System;

namespace Aemula.Emulation.Output;

// The audio-side mirror of Television: the "front door" a system's sound
// chip pushes one sample at a time into (WriteSample, the analogue of
// Television.Decode), with the whole signal-processing chain living inside
// this class and the result pulled back out through the IAudioSource Read
// contract. A system produces audio at whatever odd rate its hardware runs
// (the Atari 2600's TIA emits roughly 31.4 kHz, other chips something else
// entirely), usually from inside the same tight per-cycle tick loop that
// drives video; this class turns that stream into the fixed 48 kHz the
// playback layer wants, band-limited and drift-corrected, without the rest
// of the codebase needing to know any of the rates involved.
//
// The chain, in order, is:
//   1. a DC blocker, because a square-wave voice with a duty-cycle change
//      shifts its own DC level and that step would thump on every timbre
//      change;
//   2. a windowed-sinc anti-alias low-pass, so content above the lower of
//      the two Nyquist limits is removed before rate conversion instead of
//      folding down into the audible band;
//   3. a fractional resampler (Catmull-Rom) from InputSampleRate*(1+trim)
//      to 48 kHz, reading through a small input ring buffer that decouples
//      WriteSample (called many times per emulated scanline) from Read
//      (called once per rendered frame);
//   4. an underrun/overrun policy - Read returns short rather than blocking
//      when the buffer runs dry, and the ring caps its own backlog so a
//      paused-then-resumed emulator does not dump a half-second latency
//      spike into the speakers all at once.
//
// Single-threaded by design: WriteSample and Read are called from the same
// thread, interleaved (tick loop fills, frame boundary drains). There are
// no locks and none are needed; do not call it from two threads.
public sealed class AudioOutput : IAudioSource
{
    // What Read always produces, regardless of InputSampleRate. 48 kHz is
    // the near-universal device rate and comfortably above audible range.
    public const int OutputSampleRate = 48_000;

    // Windowed-sinc FIR length. Odd, so the filter is a true linear-phase
    // Type-I FIR with a real centre tap and an integer group delay. 63 taps
    // with a Blackman window gives a stopband around -74 dB and a
    // transition band narrow enough that the passband still reaches close
    // to Nyquist even when InputSampleRate and OutputSampleRate are within
    // a factor of two of each other (e.g. a ~31 kHz source into 48 kHz).
    private const int FirTaps = 63;

    // Passband edge as a fraction of the lower of the input/output sample
    // rates - i.e. 90% of the lower Nyquist. Below both Nyquist limits so
    // nothing aliases through the resampler, but high enough to keep the
    // full audio band intact for any realistic source rate.
    private const double AntiAliasCutoffFraction = 0.45;

    // DC blocker corner frequency in Hz. Low enough to leave the bass end
    // of the audio band alone, high enough that a duty-cycle DC step
    // settles out in a few milliseconds rather than being audible as a
    // slow drift. The pole coefficient is derived from this and the input
    // rate in the constructor, so the corner stays put regardless of how
    // fast the source runs (a fixed coefficient would move the corner with
    // the rate).
    private const double DcBlockerCutoffHz = 20.0;

    // How much un-drained input the ring is allowed to hold before it
    // starts discarding the oldest samples. Half a second: enough that a
    // consumer briefly falling behind loses nothing, small enough that
    // resuming from a long pause does not dump a huge, audibly delayed
    // block of stale audio - it just skips forward to roughly live.
    private const double MaxBacklogSeconds = 0.5;

    // Matches the IAudioSource contract's stated bound; anything larger is
    // pitch error, not drift correction.
    private const double MaxAbsTrim = 0.02;

    public double InputSampleRate { get; }

    // Playback-side gain, applied only in Read. Not part of the DSP chain,
    // so changing it never disturbs filter or resampler state.
    public float MasterVolume { get; set; } = 1f;

    // --- DC blocker state: y[n] = x[n] - x[n-1] + R*y[n-1] ---
    private readonly double _dcBlockerR;
    private double _dcLastInput;
    private double _dcLastOutput;

    // --- anti-alias FIR: fixed coefficients + a circular history ring ---
    private readonly float[] _firCoefficients = new float[FirTaps];
    private readonly float[] _firHistory = new float[FirTaps];
    private int _firHistoryPos;

    // --- input ring buffer of band-limited samples, absolutely indexed ---
    // A slot for absolute sample index i lives at _ring[i & _ringMask]. The
    // ring only ever holds the window [_tail, _head); _head is the running
    // count of samples ever written, _tail the oldest index still retained.
    private readonly float[] _ring;
    private readonly int _ringMask;
    private readonly long _maxBacklogSamples;
    private long _head;
    private long _tail;

    // Fractional read cursor, in the same absolute-index space as
    // _head/_tail. Starts at 1 (not 0) so the Catmull-Rom interpolator
    // always has one sample of history (index floor(pos)-1) available
    // without the cursor ever having to move backwards to get it.
    private double _readCursor = 1.0;

    private double _trim;

    public AudioOutput(double inputSampleRate)
    {
        if (inputSampleRate <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inputSampleRate), inputSampleRate, "Input sample rate must be positive.");
        }

        InputSampleRate = inputSampleRate;

        // Map the DC-blocker corner frequency to its pole via the standard
        // one-pole relation R = e^(-2*pi*fc/fs); at a ~31 kHz source this
        // lands near the classic R ~= 0.996.
        _dcBlockerR = Math.Exp(-2.0 * Math.PI * DcBlockerCutoffHz / inputSampleRate);

        BuildAntiAliasFilter(inputSampleRate);

        // Size the ring to the next power of two above the backlog cap plus
        // a few samples of interpolation headroom, so index wrapping is a
        // single mask.
        _maxBacklogSamples = (long)(inputSampleRate * MaxBacklogSeconds) + 1;
        var capacity = NextPowerOfTwo((int)_maxBacklogSamples + 8);
        _ring = new float[capacity];
        _ringMask = capacity - 1;
    }

    // One input sample in. Runs it through the DC blocker and the
    // anti-alias FIR, then appends the band-limited result to the ring,
    // discarding the oldest sample if the backlog cap is now exceeded.
    public void WriteSample(float sample)
    {
        // DC blocker.
        var x = (double)sample;
        var y = x - _dcLastInput + _dcBlockerR * _dcLastOutput;
        _dcLastInput = x;
        _dcLastOutput = y;

        // Anti-alias FIR. _firHistory is a circular buffer; walk it
        // backwards from the just-written newest sample, pairing each past
        // input with its coefficient.
        _firHistory[_firHistoryPos] = (float)y;
        double acc = 0.0;
        var idx = _firHistoryPos;
        for (var k = 0; k < FirTaps; k++)
        {
            acc += _firCoefficients[k] * _firHistory[idx];
            if (--idx < 0)
            {
                idx += FirTaps;
            }
        }
        if (++_firHistoryPos == FirTaps)
        {
            _firHistoryPos = 0;
        }

        // Append to the ring.
        _ring[_head & _ringMask] = (float)acc;
        _head++;

        // Overrun policy: keep only the most recent _maxBacklogSamples. If
        // the read cursor was pointing into the part we just dropped, jump
        // it forward to the new oldest retained sample (plus one, to keep
        // the interpolator's history sample valid) - the consumer skips
        // ahead to roughly live rather than replaying stale audio later.
        if (_head - _tail > _maxBacklogSamples)
        {
            _tail = _head - _maxBacklogSamples;
            if (_readCursor < _tail + 1)
            {
                _readCursor = _tail + 1;
            }
        }
    }

    public int AvailableOutputSamples
    {
        get
        {
            // Samples ahead of the cursor that are usable as the "c" and
            // "d" points of a Catmull-Rom span (floor(cursor)+1 and +2).
            var usableAhead = _head - _readCursor - 2.0;
            if (usableAhead <= 0.0)
            {
                return 0;
            }

            return (int)(usableAhead / CurrentStep);
        }
    }

    public int Read(Span<float> destination)
    {
        var step = CurrentStep;
        var volume = MasterVolume;
        var produced = 0;

        for (; produced < destination.Length; produced++)
        {
            var i0 = (long)Math.Floor(_readCursor);

            // Need history sample i0-1 still in the ring, and both forward
            // samples i0+1 and i0+2 already written. If not, we have
            // underrun - stop here and let the tail be filled with silence.
            if (i0 - 1 < _tail || i0 + 2 >= _head)
            {
                break;
            }

            var frac = _readCursor - i0;
            var s = CatmullRom(
                _ring[(i0 - 1) & _ringMask],
                _ring[i0 & _ringMask],
                _ring[(i0 + 1) & _ringMask],
                _ring[(i0 + 2) & _ringMask],
                frac);

            destination[produced] = s * volume;
            _readCursor += step;
        }

        // Underrun tail: explicit silence, so a consumer that ignores the
        // return value still gets a clean buffer.
        destination[produced..].Clear();

        // Release input the cursor has moved past, keeping two samples
        // behind it (the next span's history point, with margin).
        var releaseTo = (long)Math.Floor(_readCursor) - 2;
        if (releaseTo > _tail)
        {
            _tail = releaseTo;
        }

        return produced;
    }

    // Clamp to the contract's bound and store; picked up by the next Read
    // via CurrentStep. Read resamples on demand from the ring rather than
    // into any intermediate buffer, so "takes effect on the next Read" is
    // as immediate as a rate change can be.
    public void SetResampleTrim(double trim)
    {
        if (double.IsNaN(trim))
        {
            trim = 0.0;
        }

        _trim = Math.Clamp(trim, -MaxAbsTrim, MaxAbsTrim);
    }

    // Back to power-on: empty the ring, zero every filter's memory, recentre
    // the read cursor. MasterVolume and the resample trim are left alone -
    // they are consumer-owned playback settings, not signal state.
    public void Reset()
    {
        _dcLastInput = 0.0;
        _dcLastOutput = 0.0;

        Array.Clear(_firHistory);
        _firHistoryPos = 0;

        Array.Clear(_ring);
        _head = 0;
        _tail = 0;
        _readCursor = 1.0;
    }

    // Input samples consumed per output sample. trim > 0 shrinks the step,
    // so more output samples come out per second of input - i.e. it
    // multiplies the effective output rate by (1 + trim), which is the
    // direction the IAudioSource contract specifies.
    private double CurrentStep => InputSampleRate / (OutputSampleRate * (1.0 + _trim));

    // Catmull-Rom cubic through b and c, with a and d as the outer control
    // points; t in [0, 1) is the position between b and c. Cubic rather
    // than linear because linear interpolation of an already
    // band-limited stream still adds a few tenths of a dB of high-frequency
    // droop that a listener can hear on a sustained high note.
    private static float CatmullRom(float a, float b, float c, float d, double t)
    {
        var t2 = t * t;
        var t3 = t2 * t;
        return (float)(0.5 * (
            2.0 * b
            + (-a + c) * t
            + (2.0 * a - 5.0 * b + 4.0 * c - d) * t2
            + (-a + 3.0 * b - 3.0 * c + d) * t3));
    }

    // Blackman-windowed sinc low-pass, normalised to unity DC gain. Built
    // once: the cutoff is fixed for the life of the object because both
    // sample rates are.
    private void BuildAntiAliasFilter(double inputSampleRate)
    {
        var cutoffHz = AntiAliasCutoffFraction * Math.Min(inputSampleRate, OutputSampleRate);
        var fc = cutoffHz / inputSampleRate; // cycles per input sample, in (0, 0.5)
        var mid = (FirTaps - 1) / 2.0;

        double sum = 0.0;
        for (var k = 0; k < FirTaps; k++)
        {
            var n = k - mid;
            double sinc;
            if (Math.Abs(n) < 1e-9)
            {
                sinc = 2.0 * fc;
            }
            else
            {
                sinc = Math.Sin(2.0 * Math.PI * fc * n) / (Math.PI * n);
            }

            var window =
                0.42
                - 0.5 * Math.Cos(2.0 * Math.PI * k / (FirTaps - 1))
                + 0.08 * Math.Cos(4.0 * Math.PI * k / (FirTaps - 1));

            var tap = sinc * window;
            _firCoefficients[k] = (float)tap;
            sum += tap;
        }

        for (var k = 0; k < FirTaps; k++)
        {
            _firCoefficients[k] = (float)(_firCoefficients[k] / sum);
        }
    }

    private static int NextPowerOfTwo(int value)
    {
        var result = 1;
        while (result < value)
        {
            result <<= 1;
        }

        return result;
    }
}
