using System;
using System.Numerics;
using Aemula.Emulation.Systems;
using Hexa.NET.ImGui;
using Hexa.NET.SDL3;

namespace Aemula.UI;

// The default window: a full-bleed Television render with a menu bar, owning
// keyboard/gamepad input to the emulated system. No region overlays, no
// crosshair, no sidebar, no hover tooltip - deliberately just the picture.
// It also owns the audio path: an SDL playback device stream, opened per
// system in SetSystem exactly like the video texture view, topped up each
// frame from EmulatedSystem.Audio (see PumpAudio) and torn down in Dispose.
public sealed class EmulationWindow : IDisposable
{
    // What the menu bar needs from Program. Program owns the system lifecycle
    // (a system swap disposes GPU resources the debugger windows hold, so it
    // has to happen between frames), so the menu only ever *requests* things.
    public sealed record Callbacks(
        Func<SystemCatalogEntry> CurrentEntry,
        Action<SystemCatalogEntry> ChooseSystem,
        Action OpenRom,
        Action ResetSystem,
        Action Quit,
        Func<bool> IsDebuggerVisible,
        Action ToggleDebugger);

    private readonly SDLGPUDevicePtr _gpuDevice;
    private readonly ImGuiWindowContext _context;
    private readonly Callbacks _callbacks;

    private EmulatedSystem? _system;
    private TelevisionTextureView? _textureView;

    // The fixed rate every IAudioSource resamples to - AudioOutput and Speaker
    // both expose it as OutputSampleRate = 48_000. Kept as a bare constant
    // here rather than referencing either of those types, so the window
    // depends only on the IAudioSource abstraction.
    private const int AudioSampleRate = 48_000;

    // SDL doesn't surface its SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK macro through
    // this binding; its value is the all-ones SDL_AudioDeviceID.
    private const uint AudioDeviceDefaultPlayback = 0xFFFFFFFF;

    // Null (default-constructed) until SetSystem opens one; guarded with
    // IsNull the same way the SDL pointer handles elsewhere are.
    private SDLAudioStreamPtr _audioStream;

    // Reused across frames for the IAudioSource.Read into PutAudioStreamData
    // hand-off; grown on demand, never shrunk.
    private float[] _audioScratch = [];

    private bool _muted;
    private float _volume = 1f;

    public EmulationWindow(SDLGPUDevicePtr gpuDevice, ImGuiWindowContext context, Callbacks callbacks)
    {
        _gpuDevice = gpuDevice;
        _context = context;
        _callbacks = callbacks;
    }

    public ImGuiWindowContext Context => _context;

    // Every system exposes EmulatedSystem.Television and EmulatedSystem.Audio
    // (both concrete on the base class - Audio falls back to a silent
    // singleton), so this just grabs them - no per-system branching.
    public void SetSystem(EmulatedSystem system)
    {
        _system = system;

        _textureView?.Dispose();
        _textureView = new TelevisionTextureView(system.Television);
        _textureView.CreateGraphicsResources(_gpuDevice);

        // Drop any samples the previous system left buffered so nothing stale
        // crosses the discontinuity as an audible pop.
        system.Audio.Reset();

        if (!_audioStream.IsNull)
        {
            SDL.DestroyAudioStream(_audioStream);
            _audioStream = default;
        }

        var spec = new SDLAudioSpec
        {
            Format = SDLAudioFormat.F32Le,
            Channels = 1,
            Freq = AudioSampleRate,
        };

        // Push model: no callback, PumpAudio feeds the stream each frame.
        _audioStream = SDL.OpenAudioDeviceStream(
            AudioDeviceDefaultPlayback, in spec, default(SDLAudioStreamCallback), nint.Zero);
        if (_audioStream.IsNull)
        {
            // Non-fatal: the app runs on silently without a playback device.
            Console.WriteLine($"Warning: SDL_OpenAudioDeviceStream(): {SDL.GetErrorS()}");
        }
        else
        {
            SDL.ResumeAudioStreamDevice(_audioStream);
        }
    }

    // Forwards to the system unconditionally - the only ImGui interactables
    // here are the menus (which capture only while open, and the caller
    // routes menu-time keys away from here). Gamepad forwarding is the
    // caller's job: those events carry no window id.
    public void HandleKeyEvent(SDLKeyboardEvent keyEvent)
    {
        _system?.OnKeyEvent(keyEvent);
    }

    public void RenderFrame(EmulatorTime time, SDLGPUCommandBufferPtr commandBuffer, Vector4 clearColor)
    {
        _context.NewFrame();

        _textureView?.Prepare(commandBuffer);

        DrawMenuBar();
        DrawPicture();
        DrawStatusBar();

        // Feed the playback device from the samples this frame's emulation
        // tick just produced. A silent system's NullAudioSource hands back
        // zeros, which is exactly what should be queued.
        PumpAudio();

        ImGui.Render();
        _context.Render(commandBuffer, clearColor);
    }

    // Once per rendered frame: keep roughly TargetLatencySamples of audio
    // queued on the device, drawing the shortfall from whatever IAudioSource
    // the current system exposes, then run the drift-trim feedback loop and
    // apply mute / volume. Robust to Program's coarse frame-time clamp - the
    // queued buffer absorbs the jitter and the trim corrects the slow drift.
    private unsafe void PumpAudio()
    {
        if (_audioStream.IsNull || _system == null)
        {
            return;
        }

        // ~60 ms at 48 kHz: deep enough to ride out frame-time jitter and
        // Program's 17 ms delta clamp without a latency a player would notice.
        const int targetLatencySamples = 2880;

        var audio = _system.Audio;

        var queued = SDL.GetAudioStreamQueued(_audioStream) / sizeof(float);
        var need = targetLatencySamples - queued;
        if (need > 0)
        {
            if (_audioScratch.Length < need)
            {
                _audioScratch = new float[need];
            }

            // Read zero-fills its own tail on underrun, so the whole 'need'
            // span is always valid to queue (the unwritten tail is silence).
            audio.Read(_audioScratch.AsSpan(0, need));
            fixed (float* p = _audioScratch)
            {
                SDL.PutAudioStreamData(_audioStream, p, need * sizeof(float));
            }
        }

        // Proportional control on the same queued figure: buffer running long
        // -> ask the source for slightly fewer output samples per second, and
        // vice versa. Gain is deliberately small and the result is clamped
        // well inside the IAudioSource contract's +/-0.02; tune later.
        const double trimGain = 0.05;
        var trim = Math.Clamp(
            trimGain * (queued - targetLatencySamples) / targetLatencySamples,
            -0.02,
            0.02);
        audio.SetResampleTrim(trim);

        SDL.SetAudioStreamGain(_audioStream, _muted ? 0f : _volume);
    }

    private unsafe void DrawMenuBar()
    {
        if (!ImGui.BeginMainMenuBar())
        {
            return;
        }

        var currentEntry = _callbacks.CurrentEntry();

        if (ImGui.BeginMenu("File"u8))
        {
            if (ImGui.BeginMenu("System"u8))
            {
                foreach (var entry in SystemCatalog.Entries)
                {
                    var selected = entry.Id == currentEntry.Id;
                    if (ImGui.MenuItem(entry.DisplayName, (byte*)null, selected, true) && !selected)
                    {
                        _callbacks.ChooseSystem(entry);
                    }
                }

                ImGui.EndMenu();
            }

            if (ImGui.MenuItem("Open ROM…"u8, "Ctrl+O"u8, false, currentEntry.Rom != RomRequirement.None))
            {
                _callbacks.OpenRom();
            }

            if (ImGui.MenuItem("Reset"u8, "Ctrl+R"u8))
            {
                _callbacks.ResetSystem();
            }

            ImGui.Separator();

            if (ImGui.MenuItem("Quit"u8))
            {
                _callbacks.Quit();
            }

            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("View"u8))
        {
            if (ImGui.MenuItem("Debugger"u8, "`"u8, _callbacks.IsDebuggerVisible(), true))
            {
                _callbacks.ToggleDebugger();
            }

            ImGui.Separator();

            if (ImGui.MenuItem("Mute"u8, ""u8, _muted, true))
            {
                _muted = !_muted;
            }

            ImGui.SliderFloat("Volume"u8, ref _volume, 0f, 1f);

            ImGui.EndMenu();
        }

        ImGui.EndMainMenuBar();
    }

    // Height of the console-control status bar, or 0 when the current system
    // has no such controls. Depends on the live ImGui style, so it's only
    // valid to call inside a frame.
    private float MeasureStatusBarHeight()
    {
        if (_system is not { ConsoleControls.Count: > 0 })
        {
            return 0f;
        }

        return ImGui.GetFrameHeight() + ImGui.GetStyle().WindowPadding.Y * 2f;
    }

    private void DrawPicture()
    {
        if (_textureView == null)
        {
            return;
        }

        // WorkPos/WorkSize already exclude the main menu bar, so a window
        // filling the work area sits neatly below it - minus the strip the
        // status bar reserves along the bottom.
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.WorkPos);
        ImGui.SetNextWindowSize(viewport.WorkSize - new Vector2(0f, MeasureStatusBarHeight()));

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0f, 0f, 0f, 1f));

        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoDecoration
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoBringToFrontOnFocus
            | ImGuiWindowFlags.NoNav
            | ImGuiWindowFlags.NoScrollWithMouse;

        if (ImGui.Begin("##emulation"u8, flags))
        {
            _textureView.DrawImage(activeVideoOnly: true);
        }

        ImGui.End();

        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }

    // A single-row bar pinned to the bottom of the work area, one group of
    // widgets per ConsoleControl the current system exposes: a push button for
    // a momentary control (held closed only while the mouse is down on it) and
    // a labelled pair of radio buttons for a latching one.
    private void DrawStatusBar()
    {
        if (_system is not { ConsoleControls.Count: > 0 } system)
        {
            return;
        }

        var controls = system.ConsoleControls;
        var viewport = ImGui.GetMainViewport();
        var height = MeasureStatusBarHeight();

        ImGui.SetNextWindowPos(new Vector2(
            viewport.WorkPos.X,
            viewport.WorkPos.Y + viewport.WorkSize.Y - height));
        ImGui.SetNextWindowSize(new Vector2(viewport.WorkSize.X, height));

        // NoNav (inputs + focus): this bar is mouse-only. Without it, clicking a
        // control leaves the keyboard-nav cursor parked on it - so a later Space
        // activates that widget instead of reaching the emulated system as a
        // joystick fire, and an arrow key re-summons the nav highlight here.
        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoDecoration
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoBringToFrontOnFocus
            | ImGuiWindowFlags.NoNav
            | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse;

        if (ImGui.Begin("##console-controls"u8, flags))
        {
            for (var i = 0; i < controls.Count; i++)
            {
                if (i > 0)
                {
                    // A wide gap between controls so each group of radio
                    // buttons reads as one unit rather than running together.
                    ImGui.SameLine(0f, ImGui.GetStyle().ItemSpacing.X * 5f);
                }

                DrawConsoleControl(controls[i]);
            }
        }

        ImGui.End();
    }

    private static void DrawConsoleControl(ConsoleControl control)
    {
        switch (control.Kind)
        {
            case ConsoleControl.ControlKind.Momentary:
                ImGui.Button(control.Label);
                // Closed for exactly as long as the mouse is held on it, so a
                // game polling the switch sees a real, releasable press.
                control.Value = ImGui.IsItemActive();
                break;

            case ConsoleControl.ControlKind.Toggle:
                var value = control.Value;
                // Nudge the bare label down onto the radio buttons' text
                // baseline; without this the first control's label rides high
                // (later ones inherit the offset from the widget they follow).
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted($"{control.Label}:");

                // Both positions are always shown; the filled one is current.
                // '###' scopes each button's id to this control so two toggles
                // that share a caption (e.g. "B" difficulty) don't collide.
                ImGui.SameLine();
                if (ImGui.RadioButton($"{control.OffLabel}###{control.Label}-off", !value))
                {
                    control.Value = false;
                }

                ImGui.SameLine();
                if (ImGui.RadioButton($"{control.OnLabel}###{control.Label}-on", value))
                {
                    control.Value = true;
                }
                break;
        }
    }

    public void Dispose()
    {
        if (!_audioStream.IsNull)
        {
            SDL.DestroyAudioStream(_audioStream);
            _audioStream = default;
        }

        _textureView?.Dispose();
    }
}
