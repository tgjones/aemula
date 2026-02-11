using System;

namespace Aemula.Debugging;

public readonly struct DebuggerStepMode(string label, Func<bool> shouldStop, Action? setup = null)
{
    public readonly string Label = label;
    public readonly Action? Setup = setup;
    public readonly Func<bool> ShouldStop = shouldStop;
}
