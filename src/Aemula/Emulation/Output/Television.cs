using System;

namespace Aemula.Emulation.Output;

public class Television
{

}

public class Oscillator
{
    private readonly double _deltaPerSecond;

    private float _timeSinceLastReset;
    private float _value;

    public Oscillator(float freeRunFrequency, int resolution)
    {
        _deltaPerSecond = (1.0f / freeRunFrequency) / resolution;
    }

    public OscillatorUpdateResult Update(float deltaTime)
    {
        //if (thing > _timeSinceLastReset)
        //{
        //    // Trigger a sync
        //    _timeSinceLastReset = 0;
        //    return OscillatorUpdateResult.TriggerSync;
        //}

        throw new System.NotImplementedException();
    }

    public void OnSyncSignalDetected()
    {
        // If the oscillator's current state is close to the point where it
        // would naturally trigger a retrace, trigger a retrace at this
        // precise moment.

        // Calculate the new frequency, and update _deltaPerSecond.
    }

    public float GetValue()
    {
        // return (float)Math.Sin(2 * Math.PI * Phase);

        throw new System.NotImplementedException();
    }
}

public enum OscillatorUpdateResult
{
    None,
    TriggerSync,
}
