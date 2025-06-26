namespace Aemula.Chips.Mos6532;

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
        Value = (byte)(value - 1);
        _interval = interval;
        _cyclesRemaining = (ushort)(interval - 1);
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
