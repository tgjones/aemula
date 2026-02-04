using System;

namespace Aemula.Debugging;

public readonly struct DebuggerStepMode
{
    public readonly string Label;
    public readonly Action? Setup;
    public readonly Func<bool> ShouldStop;

    public DebuggerStepMode(string label, Func<bool> shouldStop, Action? setup = null)
    {
        Label = label;
        Setup = setup;
        ShouldStop = shouldStop;
    }
}
