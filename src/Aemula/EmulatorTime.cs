using System;

namespace Aemula;

public readonly struct EmulatorTime
{
    public readonly TimeSpan TotalTime;
    public readonly TimeSpan ElapsedTime;

    public EmulatorTime(TimeSpan totalTime, TimeSpan elapsedTime)
    {
        TotalTime = totalTime;
        ElapsedTime = elapsedTime;
    }
}

public static class TimeSpanExtensions
{
    public static uint ToSystemTicks(this TimeSpan duration, ulong cyclesPerSecond)
    {
        return (uint)Math.Round(cyclesPerSecond * duration.TotalSeconds);
    }
}
