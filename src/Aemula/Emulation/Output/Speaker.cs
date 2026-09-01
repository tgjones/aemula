using System;

namespace Aemula.Emulation.Output;

// The audio-side model of a directly-driven speaker cone: the Apple II's
// $C030 soft switch, the PC speaker, a Sinclair-style beeper. Unlike
// AudioOutput there is no audio-out signal to receive here - one pin drives a
// transistor wired straight to the cone, so the "signal" is nothing more than
// the instants that pin flipped. Almost all of the machine's multi-MHz
// timeline carries no audio information at all (the bit only ever moves on a
// $C03X access), so sampling the pin on a fixed schedule and decimating would
// be reconstructing a periodic stream that was never there.
//
// The model is therefore edge-driven. Level is the pin; its setter reacts to
// a transition, and Tick() - called once per tickRate clock - advances a
// free-running position so each transition is placed in time purely by call
// order, the same way every chip in this codebase locates its pin activity
// (no pin anywhere here carries a timestamp). Each transition is spliced into
// the 48 kHz output with BLEP (band-limited step) synthesis: a short
// Blackman-windowed-sinc step stands in for the ideal discontinuity so the
// result contains nothing above Nyquist, with no periodic-rate filter
// involved at any point.
//
// What Speaker still shares with AudioOutput is the output side. The emulator
// is paced by wall clock, not by the audio device, so the count of samples
// produced and the count consumed drift apart over minutes regardless of how
// the samples were synthesised. Read therefore pulls through the same
// near-unity fractional read cursor, nudged by the same SetResampleTrim drift
// lever AudioOutput exposes - just with linear interpolation, over an output
// ring that is already at 48 kHz and already band-limited (BLEP synthesis did
// the anti-alias job at the point each edge went in), so none of AudioOutput's
// anti-alias FIR / arbitrary-ratio resampler stack is needed on top.
//
// It does keep a one-pole DC blocker on that output, though. A real cone is a
// mass on a spring: a held drive level holds it silently off-centre and it
// relaxes back toward rest over a few milliseconds - a constant displacement
// is not sound - and every downstream playback path AC-couples anyway. Without
// this, a machine left idle after an odd number of $C030 clicks (the boot
// ROM's startup bell is one) would pin the output at +/-Amplitude on every
// sample forever. A ~20 Hz corner leaves clicks and the lowest beeps intact
// while bleeding that pedestal away.
//
// Single-threaded by design, exactly like AudioOutput: Tick/Level and Read
// run interleaved on the one emulation thread. No locks, and none are needed.
public sealed class Speaker : IAudioSource
{
    // What Read always produces, matching AudioOutput so a consumer opens one
    // 48 kHz device regardless of which IAudioSource a system hands it.
    public const int OutputSampleRate = 48_000;

    // The cone excursion a fresh transition drives toward: Level == true ->
    // +Amplitude, Level == false -> -Amplitude. 0.6 leaves comfortable headroom
    // in [-1, 1] for the small band-limiting overshoot on each edge. A fresh
    // Speaker starts from a true rest at 0, not at -Amplitude: a machine that
    // never touches its speaker must read back as pure silence, so only the
    // first transition onward does the cone swing the full range. The DC
    // blocker below then relaxes any held level back toward 0, so a sustained
    // level is silent rather than a permanent pedestal.
    internal const double Amplitude = 0.6;

    // DC blocker corner frequency, in Hz, on the 48 kHz output. Low enough to
    // pass the lowest Apple II beeps and the body of a click essentially
    // untouched (a click's energy is well above it), high enough that a held
    // level decays to inaudible in a few milliseconds. See the type remarks
    // for why a directly-driven cone needs this even though BLEP output is
    // already band-limited.
    private const double DcBlockerCutoffHz = 20.0;

    // BLEP kernel geometry. The spliced step is the running integral of a
    // windowed sinc spanning BlepHalfWidth output samples each side of the
    // edge, so a single transition only ever perturbs BlepWidth samples. Four
    // each side is deliberately short - a longer kernel would smear the click
    // over more time for no audible gain on a 1-bit speaker - and a short
    // kernel rings a few percent past its target on each edge, which is why
    // Amplitude leaves headroom below 1. The kernel is precomputed at
    // BlepPhases sub-sample offsets and linearly interpolated at insert time;
    // 32 phases keeps the edge-position error far below the "recognizably
    // correct" bar this codebase's audio aims for, for a table of only
    // (BlepPhases + 1) * BlepWidth doubles.
    private const int BlepHalfWidth = 4;
    private const int BlepWidth = 2 * BlepHalfWidth;
    private const int BlepPhases = 32;

    // Sinc cutoff as a fraction of Nyquist. Pulling it in from 1.0 leaves a
    // small transition band, which tames the step-response overshoot of such
    // a short kernel from ~9% (bare Gibbs) to a couple of percent while
    // costing only a slightly softer edge.
    private const double BlepCutoff = 0.9;

    // Output-ring backlog cap, same rationale and value as AudioOutput: a
    // paused-then-resumed emulator can finalise a big block of samples in one
    // go, and without a cap that becomes unbounded latency. Half a second is
    // long enough that a consumer briefly falling behind loses nothing.
    private const double MaxBacklogSeconds = 0.5;

    // Matches the IAudioSource contract's stated bound; anything larger is
    // pitch error, not drift correction.
    private const double MaxAbsTrim = 0.02;

    // The rate Tick() is called at (e.g. the Apple II's 14.318 MHz master
    // clock). Used only to place edges in time - never to sample anything.
    public double TickRate { get; }

    // Playback-side gain, applied only in Read, so changing it never disturbs
    // synthesis or resampler state.
    public float MasterVolume { get; set; } = 1f;

    private readonly double _samplesPerTick;

    // _blepDelta[phase][tap] is the value added into the delta ring at output
    // sample (edgeSample - BlepHalfWidth + 1 + tap) for a unit step whose
    // fractional position is phase / BlepPhases. Built once in the constructor.
    private readonly double[][] _blepDelta;

    // --- delta ring: first differences of the band-limited step, awaiting
    // integration. Absolutely indexed (slot for sample i is _deltaRing[i &
    // _deltaMask]); only ever holds the handful of samples around the current
    // position, so it is deliberately tiny. ---
    private readonly float[] _deltaRing;
    private readonly int _deltaMask;

    // --- output ring: finalised 48 kHz samples, absolutely indexed, holding
    // the window [_outTail, _outHead). _outHead is the running count of
    // samples ever finalised, _outTail the oldest still retained. ---
    private readonly float[] _outRing;
    private readonly int _outMask;
    private readonly long _maxBacklogSamples;
    private long _outHead;
    private long _outTail;

    // Fractional read cursor, in the same absolute-index space as _outHead /
    // _outTail. Linear interpolation needs only floor(cursor) and its
    // successor, so it can start at 0 with no history-sample margin.
    private double _readCursor;
    private double _trim;

    // Free-running tick count; the output position is recomputed from it
    // rather than accumulated (see CurrentPosition).
    private long _tickCount;

    // Running integral of the delta ring = the current cone level. Carries
    // each spliced step's full height forward with no further work - that
    // carried DC part is the remainder of the step residual.
    private double _integrator;

    // One-pole DC blocker on the finalised output, y[n] = x[n] - x[n-1] +
    // R*y[n-1]. _dcBlockerR is e^(-2*pi*fc/OutputSampleRate), computed once in
    // the constructor; the two history values are the last input and output.
    private readonly double _dcBlockerR;
    private double _dcLastInput;
    private double _dcLastOutput;

    // The last settled level a transition moved to, so the next transition's
    // step height is (newLevel - _currentLevel): +/-Amplitude for the first
    // edge out of rest, +/-2*Amplitude for every toggle after.
    private double _currentLevel;
    private bool _level;

    public Speaker(double tickRate)
    {
        if (tickRate <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tickRate), tickRate, "Tick rate must be positive.");
        }

        TickRate = tickRate;
        _samplesPerTick = OutputSampleRate / tickRate;

        _dcBlockerR = Math.Exp(-2.0 * Math.PI * DcBlockerCutoffHz / OutputSampleRate);

        _blepDelta = BuildBlepTable();

        _maxBacklogSamples = (long)(OutputSampleRate * MaxBacklogSeconds) + 1;
        var capacity = NextPowerOfTwo((int)_maxBacklogSamples + BlepWidth + 8);
        _outRing = new float[capacity];
        _outMask = capacity - 1;

        // 64 comfortably spans the live delta window (an edge writes
        // BlepWidth slots starting BlepHalfWidth-1 behind the current
        // position; finalisation trails BlepHalfWidth behind it, so the live
        // span is ~2*BlepWidth slots) with room to spare, and a power of two
        // keeps indexing a single mask.
        _deltaRing = new float[64];
        _deltaMask = _deltaRing.Length - 1;
    }

    // The pin. The setter is a no-op unless the value actually changes; on a
    // real transition it splices a band-limited step from the old settled
    // level to the new one into the output at the exact fractional-sample
    // position the tick counter is at right now.
    public bool Level
    {
        get => _level;
        set
        {
            if (value == _level)
            {
                return;
            }

            _level = value;

            var target = value ? Amplitude : -Amplitude;
            var delta = target - _currentLevel;
            _currentLevel = target;

            var position = CurrentPosition;
            var edgeSample = (long)position;
            var frac = position - edgeSample;

            // Linearly interpolate the kernel between the two nearest
            // precomputed sub-sample phases.
            var phase = frac * BlepPhases;
            var phase0 = (int)phase;
            var phaseFrac = phase - phase0;
            var lower = _blepDelta[phase0];
            var upper = _blepDelta[phase0 + 1];

            var baseIndex = edgeSample - BlepHalfWidth + 1;
            for (var tap = 0; tap < BlepWidth; tap++)
            {
                var weight = lower[tap] + (upper[tap] - lower[tap]) * phaseFrac;
                _deltaRing[(baseIndex + tap) & _deltaMask] += (float)(weight * delta);
            }
        }
    }

    // Called once per TickRate clock - advances Speaker's own free-running
    // position, then finalises any output samples that no future edge can
    // still reach and hands them to the output ring.
    public void Tick()
    {
        _tickCount++;

        // An edge splices taps from (edgeSample - BlepHalfWidth + 1) onward
        // and the position only ever advances, so every output sample more
        // than BlepHalfWidth behind the current position is settled for good.
        var limit = (long)CurrentPosition - BlepHalfWidth + 1;
        while (_outHead < limit)
        {
            var slot = (int)(_outHead & _deltaMask);
            _integrator += _deltaRing[slot];
            _deltaRing[slot] = 0f;

            // One-pole DC blocker: the integrator is the raw cone level, which
            // a held drive would otherwise leave sitting at +/-Amplitude
            // forever. This relaxes it back toward 0 with a ~20 Hz corner -
            // clicks and beeps pass, a static level does not.
            var blocked = _integrator - _dcLastInput + _dcBlockerR * _dcLastOutput;
            _dcLastInput = _integrator;
            _dcLastOutput = blocked;

            _outRing[_outHead & _outMask] = (float)blocked;
            _outHead++;

            // Backlog cap: drop the oldest, and drag the read cursor up to
            // the new floor if it was pointing into what we just dropped, so
            // a resumed emulator skips forward to roughly live rather than
            // replaying a stale block later.
            if (_outHead - _outTail > _maxBacklogSamples)
            {
                _outTail = _outHead - _maxBacklogSamples;
                if (_readCursor < _outTail)
                {
                    _readCursor = _outTail;
                }
            }
        }
    }

    public int AvailableOutputSamples
    {
        get
        {
            // Samples ahead of the cursor usable as the two points of a
            // linear-interpolation span (floor(cursor) and its successor).
            var usableAhead = _outHead - _readCursor - 1.0;
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

            // Need i0 still retained and its successor already finalised. If
            // not, we have underrun - stop and let the tail fill with silence.
            if (i0 < _outTail || i0 + 1 >= _outHead)
            {
                break;
            }

            var frac = (float)(_readCursor - i0);
            var a = _outRing[i0 & _outMask];
            var b = _outRing[(i0 + 1) & _outMask];

            // Clamp to the nominal range. A held level relaxes to 0 through the
            // DC blocker, so a later full cone swing out of that relaxed rest
            // is a step of ~2*Amplitude and its band-limited edge briefly
            // reaches past +/-1 - exactly the transient a real AC-coupled cone
            // makes when the drive flips after a long idle, but the sample must
            // still be in range for the device.
            var s = a + (b - a) * frac;
            destination[produced] = Math.Clamp(s, -1f, 1f) * volume;
            _readCursor += step;
        }

        // Underrun tail: explicit silence, so a consumer that ignores the
        // return value still gets a clean buffer (matches AudioOutput and
        // NullAudioSource).
        destination[produced..].Clear();

        // Release output the cursor has moved past, keeping one sample behind
        // it as the next span's first point.
        var releaseTo = (long)Math.Floor(_readCursor) - 1;
        if (releaseTo > _outTail)
        {
            _outTail = releaseTo;
        }

        return produced;
    }

    // Clamp to the contract's bound and store; picked up by the next Read via
    // CurrentStep.
    public void SetResampleTrim(double trim)
    {
        if (double.IsNaN(trim))
        {
            trim = 0.0;
        }

        _trim = Math.Clamp(trim, -MaxAbsTrim, MaxAbsTrim);
    }

    // Back to power-on: empty both rings, zero the integrator and the level
    // memory, recentre the read cursor, restart the position counter.
    // MasterVolume and the resample trim are consumer-owned playback
    // settings, not signal state, and are left alone.
    public void Reset()
    {
        Array.Clear(_deltaRing);
        Array.Clear(_outRing);
        _integrator = 0.0;
        _dcLastInput = 0.0;
        _dcLastOutput = 0.0;
        _currentLevel = 0.0;
        _level = false;
        _tickCount = 0;
        _outHead = 0;
        _outTail = 0;
        _readCursor = 0.0;
    }

    // Output samples the cursor advances per output sample produced. trim > 0
    // shrinks the step, so more samples come out per second of ticks - i.e.
    // it multiplies the effective output rate by (1 + trim), the direction
    // the IAudioSource contract (and AudioOutput) specify.
    private double CurrentStep => 1.0 / (1.0 + _trim);

    // The free-running output-sample position, in 48 kHz samples, Tick() has
    // advanced to. Recomputed from the integer tick count every time rather
    // than accumulated into a double, so it cannot drift over a long session:
    // a long is exact as a double well past any realistic runtime, leaving
    // exactly one rounding in the multiply. The constant BlepHalfWidth offset
    // gives the very first edge's pre-ring real samples to land in instead of
    // running off the start of the stream, so that first step splices in at
    // its full, exact height like every one after it.
    private double CurrentPosition => BlepHalfWidth + _tickCount * _samplesPerTick;

    // Precompute the per-phase first-difference kernels. The band-limited
    // step is the running integral of a Blackman-windowed sinc; its first
    // differences are what gets summed into the delta ring, and Tick()'s
    // running integration over that ring reproduces the step and carries its
    // height onward.
    private static double[][] BuildBlepTable()
    {
        // The windowed sinc, sampled BlepPhases times per output sample over
        // [-BlepHalfWidth, +BlepHalfWidth].
        var fineLength = BlepWidth * BlepPhases + 1;
        var impulse = new double[fineLength];
        var impulseSum = 0.0;
        for (var i = 0; i < fineLength; i++)
        {
            var x = -BlepHalfWidth + (double)i / BlepPhases;
            var arg = Math.PI * BlepCutoff * x;
            var sinc = Math.Abs(arg) < 1e-12
                ? BlepCutoff
                : BlepCutoff * Math.Sin(arg) / arg;

            // Blackman window mapped onto [-BlepHalfWidth, BlepHalfWidth]:
            // 1 at the centre, 0 at the ends.
            var window =
                0.42
                + 0.5 * Math.Cos(Math.PI * x / BlepHalfWidth)
                + 0.08 * Math.Cos(2.0 * Math.PI * x / BlepHalfWidth);

            impulse[i] = sinc * window;
            impulseSum += impulse[i];
        }

        // Normalise so the reconstructed impulse has unity DC gain: its
        // integral must climb from 0 to exactly 1 across the kernel, or every
        // spliced edge would land slightly off its intended height and the
        // errors would pile up in the integrator over a long run.
        var norm = BlepPhases / impulseSum;
        var stepShape = new double[fineLength];
        var acc = 0.0;
        for (var i = 0; i < fineLength; i++)
        {
            acc += impulse[i] * norm / BlepPhases;
            stepShape[i] = acc;
        }

        var table = new double[BlepPhases + 1][];
        for (var phase = 0; phase <= BlepPhases; phase++)
        {
            var kernel = new double[BlepWidth];
            var previous = 0.0;
            var sum = 0.0;
            for (var tap = 0; tap < BlepWidth; tap++)
            {
                // Grid index of the step evaluated at
                // (tap - BlepHalfWidth + 1 - phase / BlepPhases) output
                // samples relative to the edge.
                var idx = (tap + 1) * BlepPhases - phase;
                var value = stepShape[idx];
                kernel[tap] = value - previous;
                previous = value;
                sum += kernel[tap];
            }

            // Snap the final tap so every phase's kernel sums to exactly 1.
            // The windowed step only asymptotes to 1, so without this a
            // fractional edge would leave a sub-permille DC error behind on
            // every transition and those would accumulate in the integrator.
            kernel[BlepWidth - 1] += 1.0 - sum;
            table[phase] = kernel;
        }

        return table;
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
