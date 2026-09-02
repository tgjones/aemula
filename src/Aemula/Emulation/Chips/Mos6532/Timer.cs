namespace Aemula.Emulation.Chips.Mos6532;

internal sealed class Timer
{
    /// <summary>
    /// Cycles remaining in the current interval.
    /// </summary>
    private ushort _cyclesRemaining;

    /// <summary>
    /// Interval configured via register. Can be 1, 8, 64, or 1024.
    /// </summary>
    private ushort _interval;

    /// <summary>
    /// Timer value as it is read / written through register, measured in intervals.
    /// </summary>
    public byte Value;

    public bool Expired;

    public Timer()
    {
        // According to https://atariage.com/forums/topic/256802-two-questions-about-the-pia/?do=findComment&comment=3590223,
        // the interval is set to 1024T at startup, while the actual timer value is random.

        Value = 0xAA; // "random" value

        _cyclesRemaining = 1024;

        _interval = 1024;

        Expired = false;
    }

    public void Reset(byte value, ushort interval)
    {
        // The written value lands in INTIM as-is; the first timer clock (one
        // cycle after the write) takes it to value - 1, and from there it
        // steps down once per `interval` cycles. A write of 0 therefore
        // underflows on that very next cycle. `_cyclesRemaining = 0` makes the
        // next Tick fall straight through to the decrement. Matches the 6532
        // as modelled by Stella (setTimerRegister + updateEmulation).
        Value = value;
        _interval = interval;
        _cyclesRemaining = 0;

        // Writing the timer register restarts interval counting from scratch.
        // Without this, a timer that had underflowed even once stayed latched
        // in its post-underflow "decrement Value every cycle" mode forever -
        // every later write then ignored the prescaler and expired ~interval
        // times too fast. (The power-on timer underflows during any game that
        // doesn't touch it for the first few frames, so this hit e.g. Pitfall
        // the moment its kernel first wrote TIM64T.)
        Expired = false;
    }

    public void Tick()
    {
        // Decrement cycles remaining in current interval.
        _cyclesRemaining = (ushort)(_cyclesRemaining - 1);

        // Did the cycles remaining go below 0?
        if (_cyclesRemaining == 0xFFFF)
        {
            // Decrement timer value.
            Value = (byte)(Value - 1);

            // Did the timer go below 0?
            if (Value == 0xFF)
            {
                Expired = true;
            }

            if (Expired)
            {
                // Timer is "finished" - now we should start counting down once per clock cycle.
                _cyclesRemaining = 0;
            }
            else
            {
                // Start new interval.
                _cyclesRemaining = (ushort)(_interval - 1);
            }
        }
    }
}
