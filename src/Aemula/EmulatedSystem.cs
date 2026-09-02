using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Aemula.Debugging;
using Aemula.Emulation.Output;
using Aemula.Emulation.Systems;
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

    // InputScript (the headless --input runner) drives a system through the
    // same SDL key events EmulationWindow sends. This maps that script's
    // generic control tokens ("up", "down", "left", "right", "fire", ...) to
    // the SDL keycodes this system's OnKeyEvent matches on; a system with
    // keyboard- or joystick-driven input overrides it. Console-panel buttons
    // aren't here - those are scripted through each ConsoleControls entry's
    // Mnemonic.
    public virtual IReadOnlyDictionary<string, int> InputKeyBindings => ReadOnlyDictionary<string, int>.Empty;

    // The switches and buttons on this system's physical console housing (the
    // Atari 2600's RESET / SELECT / difficulty / colour switches, and the
    // like) - hand-operated hardware with no wiring to the emulated keyboard
    // or joystick ports, which the UI surfaces as a status-bar control row.
    // Empty for a system that has no such controls.
    public virtual IReadOnlyList<ConsoleControl> ConsoleControls => [];

    // How this system's cabinet mounted its monitor. EmulationWindow rotates
    // the finished picture by this before showing it; the debugger's
    // TelevisionWindow ignores it and shows the raw raster. None for a system
    // whose display was already upright.
    public virtual ScreenRotation ScreenRotation => ScreenRotation.None;

    // Coloured gels overlaid on the picture (see ScreenOverlay), in the order
    // they should be drawn. Empty for a system with a genuinely colour (or
    // plain monochrome) display - only the black-and-white-tube-plus-cellophane
    // games need these.
    public virtual IReadOnlyList<ScreenOverlay> ScreenOverlays => [];

    public virtual Debugger? CreateDebugger() => null;

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        OnDispose();
    }

    protected virtual void OnDispose() { }
}
