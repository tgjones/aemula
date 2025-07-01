using System;
using Aemula.Debugging;
using Veldrid;

namespace Aemula;

public abstract class EmulatedSystem : IDisposable
{
    public event EventHandler ProgramLoaded;

    protected void RaiseProgramLoaded()
    {
        ProgramLoaded?.Invoke(this, EventArgs.Empty);
    }

    public abstract ulong CyclesPerSecond { get; }

    public virtual void Reset() { }

    public abstract void LoadProgram(string filePath);

    public void RunForDuration(TimeSpan duration)
    {
        var clocks = duration.ToSystemTicks(CyclesPerSecond);

        for (var i = 0; i < clocks; i++)
        {
            Tick();
        }
    }

    public abstract void Tick();

    public virtual void OnKeyEvent(KeyEvent keyEvent) { }

    public virtual Debugger CreateDebugger() => null;

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        OnDispose();
    }

    protected virtual void OnDispose() { }
}
