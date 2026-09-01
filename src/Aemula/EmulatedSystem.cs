using System;
using Aemula.Debugging;
using Aemula.Emulation.Output;
using Hexa.NET.SDL3;

namespace Aemula;

public abstract class EmulatedSystem : IDisposable
{
    public event EventHandler? ProgramLoaded;

    // Every system decodes its composite-video output through a Television,
    // fed one sample at a time from the same tick the analog summing stage
    // produces it - the same way every other signal in this emulator
    // propagates through the chips/systems that consume it, rather than a UI
    // window pulling a backlog from a ring buffer once per frame. Tooling
    // (the headless runner, benchmarks) reads frame progress off this
    // generically, without switching on concrete system type.
    public Television Television { get; } = new();

    // The same uniform-consumption story as Television, on the audio side: a
    // system with real sound overrides this to return its own AudioOutput /
    // Speaker field - a plain overridden property backed by a field, never a
    // factory call reached from this base constructor, so there is no
    // virtual-call-before-derived-construction hazard. Every soundless system
    // falls through to the shared silent singleton, so nothing in the UI ever
    // has to branch on "does this system have audio?".
    public virtual IAudioSource Audio => NullAudioSource.Instance;

    protected void RaiseProgramLoaded()
    {
        ProgramLoaded?.Invoke(this, EventArgs.Empty);
    }

    public abstract ulong CyclesPerSecond { get; }

    // Total ticks executed via RunForDuration since construction. The
    // emulation window's free-run path has no Debugger to hang a per-tick
    // event off (the debugger's own perf readout uses Debugger.Ticked), so
    // Program diffs this per perf window instead.
    public ulong TotalCycles { get; private set; }

    public virtual void Reset() { }

    public abstract void LoadProgram(string filePath);

    public void RunForDuration(TimeSpan duration)
    {
        var clocks = duration.ToSystemTicks(CyclesPerSecond);

        for (var i = 0; i < clocks; i++)
        {
            Tick();
            TotalCycles++;
        }
    }

    public abstract void Tick();

    public virtual void OnKeyEvent(SDLKeyboardEvent keyEvent) { }

    public virtual Debugger? CreateDebugger() => null;

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        OnDispose();
    }

    protected virtual void OnDispose() { }
}
