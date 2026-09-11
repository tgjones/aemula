using System;
using System.Collections.Generic;
using System.Numerics;
using Aemula;
using Aemula.Emulation.Systems;
using Hexa.NET.ImGui;
using Hexa.NET.SDL3;

namespace Aemula.UI;

// The default window: a full-bleed Television render with a menu bar, owning
// keyboard/gamepad input to the emulated system. No diagnostic region
// overlays, no crosshair, no sidebar, no hover tooltip - deliberately just
// the picture, but turned to the system's cabinet orientation
// (EmulatedSystem.ScreenRotation) with its colour gels
// (EmulatedSystem.ScreenOverlays) applied, so Space Invaders and the like
// look the way they did on the machine rather than the way the raw raster
// scans out.
// It also owns the audio path: an SDL playback device stream, opened per
// rig in SetRig exactly like the video texture view, topped up each
// frame from Rig.Audio (see PumpAudio) and torn down in Dispose.
public sealed class EmulationWindow : IDisposable
{
    // What the menu bar needs from Program. Program owns the system lifecycle
    // (a system swap disposes GPU resources the debugger windows hold, so it
    // has to happen between frames), so the menu only ever *requests* things.
    public sealed record Callbacks(
        Func<SystemDescriptor> CurrentSystem,
        Action<SystemDescriptor> ChooseSystem,
        // Soft Reset pulses the RES line (RAM and registers survive); Hard Reset
        // rebuilds the machine from cold with the same slots and the same
        // inserted media. Both run between frames, so these only request.
        Action SoftReset,
        Action HardReset,
        Action Quit,
        Func<bool> IsDebuggerVisible,
        Action ToggleDebugger,
        // The card id fitted in a given slot of the current system (null = the
        // slot is empty), and a request to fit a different one (null = empty).
        // Changing a card rebuilds the machine, so this only requests.
        Func<string, string?> SelectedSlotCard,
        Action<string, string?> ChooseSlotCard,
        // Fill a media bay (opens a file dialog) or clear one. What each bay
        // currently holds is read straight off its MediaBay console control;
        // the shell only needs to service these two requests.
        Action<MediaBay> InsertMedia,
        Action<string> EjectMedia);

    private readonly SDLGPUDevicePtr _gpuDevice;
    private readonly ImGuiWindowContext _context;
    private readonly Callbacks _callbacks;

    private Rig? _rig;
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

    // Perf readout - Program pushes the latest numbers in before each render.
    private double _perfFps;
    private double _perfMsPerFrame;
    private double _perfActualMHz;
    private double _perfNominalMHz;

    public EmulationWindow(SDLGPUDevicePtr gpuDevice, ImGuiWindowContext context, Callbacks callbacks)
    {
        _gpuDevice = gpuDevice;
        _context = context;
        _callbacks = callbacks;
    }

    public ImGuiWindowContext Context => _context;

    // Every rig exposes Rig.Television and Rig.Audio (Audio always non-null -
    // it mixes the system and peripherals, or falls back to silence), so this
    // just grabs them - no per-system branching.
    public void SetRig(Rig rig)
    {
        _rig = rig;

        _textureView?.Dispose();
        _textureView = new TelevisionTextureView(rig.Television);
        _textureView.CreateGraphicsResources(_gpuDevice);

        // Drop any samples the previous rig left buffered so nothing stale
        // crosses the discontinuity as an audible pop.
        rig.Audio.Reset();

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
        _rig?.System.OnKeyEvent(keyEvent);
    }

    public void SetPerf(double fps, double msPerFrame, double actualMHz, double nominalMHz)
    {
        _perfFps = fps;
        _perfMsPerFrame = msPerFrame;
        _perfActualMHz = actualMHz;
        _perfNominalMHz = nominalMHz;
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
        if (_audioStream.IsNull || _rig == null)
        {
            return;
        }

        // ~60 ms at 48 kHz: deep enough to ride out frame-time jitter and
        // Program's 17 ms delta clamp without a latency a player would notice.
        const int targetLatencySamples = 2880;

        var audio = _rig.Audio;

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

        var currentSystem = _callbacks.CurrentSystem();

        if (ImGui.BeginMenu("File"u8))
        {
            if (ImGui.BeginMenu("System"u8))
            {
                foreach (var descriptor in EmulatedSystems.All)
                {
                    var selected = descriptor.Id == currentSystem.Id;
                    if (ImGui.MenuItem(descriptor.DisplayName, (byte*)null, selected, true) && !selected)
                    {
                        _callbacks.ChooseSystem(descriptor);
                    }
                }

                ImGui.EndMenu();
            }

            if (currentSystem.Slots.Count > 0 && ImGui.BeginMenu("Slots"u8))
            {
                foreach (var slot in currentSystem.Slots)
                {
                    if (!ImGui.BeginMenu(slot.DisplayName))
                    {
                        continue;
                    }

                    var fitted = _callbacks.SelectedSlotCard(slot.Id);

                    if (ImGui.MenuItem("(empty)"u8, (byte*)null, fitted == null, true) && fitted != null)
                    {
                        _callbacks.ChooseSlotCard(slot.Id, null);
                    }

                    foreach (var card in slot.Cards)
                    {
                        var isFitted = fitted == card.Id;
                        if (ImGui.MenuItem(card.DisplayName, (byte*)null, isFitted, true) && !isFitted)
                        {
                            _callbacks.ChooseSlotCard(slot.Id, card.Id);
                        }
                    }

                    ImGui.EndMenu();
                }

                ImGui.EndMenu();
            }

            if (ImGui.MenuItem("Soft Reset"u8, "Ctrl+R"u8))
            {
                _callbacks.SoftReset();
            }

            if (ImGui.MenuItem("Hard Reset"u8))
            {
                _callbacks.HardReset();
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

        DrawPerfReadout();

        ImGui.EndMainMenuBar();
    }

    // Right-aligned in the menu bar: frames-per-second, per-frame update cost,
    // and the emulated clock actual-vs-nominal. Program pushes the numbers in
    // via SetPerf before each render.
    private void DrawPerfReadout()
    {
        var perfText = $"{_perfFps:F0} FPS  {_perfMsPerFrame:F2} ms  {_perfActualMHz:F2} / {_perfNominalMHz:F2} MHz";
        var perfTextSize = ImGui.CalcTextSize(perfText);
        var perfTextX = ImGui.GetWindowWidth() - perfTextSize.X - ImGui.GetStyle().ItemSpacing.X;
        if (perfTextX > ImGui.GetCursorPosX())
        {
            ImGui.SetCursorPosX(perfTextX);
        }

        // Falling more than 5% behind the nominal clock is a sign we're no
        // longer keeping up with real-time.
        if (_perfActualMHz < _perfNominalMHz * 0.95)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), perfText);
        }
        else
        {
            ImGui.TextUnformatted(perfText);
        }
    }

    // Height of the console-control status bar, or 0 when the current system
    // has no such controls. Depends on the live ImGui style, so it's only
    // valid to call inside a frame.
    private float MeasureStatusBarHeight()
    {
        if (_rig is not { ControlGroups.Count: > 0 })
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
            _textureView.DrawImageRotated(
                activeVideoOnly: true,
                _rig?.System.ScreenRotation ?? ScreenRotation.None,
                _rig?.System.ScreenOverlays ?? []);
        }

        ImGui.End();

        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }

    // A single-row bar pinned to the bottom of the work area. The system's own
    // console controls come first with no heading; each connected peripheral's
    // controls follow as their own group under the peripheral's name
    // ("Cassette"). Groups are divided by a heavy vertical rule and the controls
    // within a group by a light one. A momentary control renders as a push
    // button (closed only while the mouse is down on it), a latching one as a
    // stay-down button, a toggle as a labelled pair of radio buttons, a readout
    // as plain text.
    private void DrawStatusBar()
    {
        if (_rig is not { ControlGroups.Count: > 0 } rig)
        {
            return;
        }

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
            var spacing = ImGui.GetStyle().ItemSpacing.X;
            var frameHeight = ImGui.GetFrameHeight();

            // Adjacent controls are told apart by a rule rather than by empty
            // space. Same colour throughout: a short inset one between the
            // controls inside a group, and a slightly thicker floor-to-ceiling
            // one to fence off each peripheral's group as its own unit.
            var thinRule = MathF.Max(1f, MathF.Round(frameHeight * 0.045f));
            var boldRule = MathF.Max(2f, MathF.Round(frameHeight * 0.09f));
            var ruleColor = ImGui.GetColorU32(ImGuiCol.Separator);

            var firstGroup = true;

            foreach (var group in rig.ControlGroups)
            {
                if (!firstGroup)
                {
                    VerticalRule(boldRule, spacing * 1.5f, ruleColor, fullHeight: true);
                }

                firstGroup = false;

                if (group.Label != null)
                {
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextDisabled(group.Label);
                    ImGui.SameLine(0f, spacing);
                }

                for (var i = 0; i < group.Controls.Count; i++)
                {
                    if (i > 0)
                    {
                        VerticalRule(thinRule, spacing, ruleColor);
                    }

                    DrawConsoleControl(group.Controls[i]);
                }
            }
        }

        ImGui.End();
    }

    // Paints a vertical divider at the cursor and reserves width for it - the
    // line itself plus `pad` px of clear space on each side - so it can sit
    // between two items on the status bar's single row. Chains onto the
    // preceding item and leaves the cursor ready for the next one.
    //
    // By default the line is inset from the row's top and bottom so it reads as
    // a divider between controls, not a border around one. `fullHeight` instead
    // runs it floor-to-ceiling of the whole bar, for a group boundary that cuts
    // the entire strip.
    private static void VerticalRule(float thickness, float pad, uint color, bool fullHeight = false)
    {
        var frameHeight = ImGui.GetFrameHeight();

        ImGui.SameLine(0f, 0f);
        var origin = ImGui.GetCursorScreenPos();
        var x = MathF.Round(origin.X + pad + thickness * 0.5f);

        float top, bottom;
        if (fullHeight)
        {
            var windowTop = ImGui.GetWindowPos().Y;
            top = windowTop;
            bottom = windowTop + ImGui.GetWindowSize().Y;
        }
        else
        {
            var inset = frameHeight * 0.15f;
            top = origin.Y + inset;
            bottom = origin.Y + frameHeight - inset;
        }

        ImGui.GetWindowDrawList().AddLine(
            new Vector2(x, top),
            new Vector2(x, bottom),
            color,
            thickness);

        ImGui.Dummy(new Vector2(thickness + pad * 2f, frameHeight));
        ImGui.SameLine(0f, 0f);
    }

    private void DrawConsoleControl(ConsoleControl control)
    {
        switch (control.Kind)
        {
            case ConsoleControl.ControlKind.MediaBay:
                DrawMediaBay(control);
                break;

            case ConsoleControl.ControlKind.Momentary:
                ImGui.Button(control.Label);
                // Closed for exactly as long as the mouse is held on it, so a
                // game polling the switch sees a real, releasable press. Only
                // written on a change of level: a setter that acts on the
                // release edge (the Apple I's RESET key, which re-runs WozMon)
                // must not see a fresh "released" every frame.
                var held = ImGui.IsItemActive();
                if (held != control.Value)
                {
                    control.Value = held;
                }
                break;

            case ConsoleControl.ControlKind.Latching:
                var engaged = control.Value;
                var caption = engaged
                    ? control.OnLabel ?? control.Label
                    : control.OffLabel ?? control.Label;

                // Pressed-in tint while engaged, so it reads like a transport
                // button that stays down (a cassette deck's PLAY).
                if (engaged)
                {
                    ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive));
                }

                if (ImGui.Button($"{caption}###{control.Label}"))
                {
                    control.Value = !engaged;
                }

                if (engaged)
                {
                    ImGui.PopStyleColor();
                }
                break;

            case ConsoleControl.ControlKind.Readout:
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted($"{control.Label}: {control.Text}");
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

    // A removable-media receptacle: a button captioned with the bay name and
    // whatever is loaded ("Cartridge: Combat", "Tape: —"), opening a popup to
    // insert / replace or eject. Insert is the shell's job (it runs the file
    // dialog); the loaded name is read straight off the control.
    private void DrawMediaBay(ConsoleControl control)
    {
        var bay = control.Bay!;
        var loaded = control.Text;
        var caption = loaded == null ? $"{bay.DisplayName}: —" : $"{bay.DisplayName}: {loaded}";

        if (ImGui.Button($"{caption}###bay-{bay.Id}"))
        {
            ImGui.OpenPopup($"##bay-menu-{bay.Id}");
        }

        if (ImGui.BeginPopup($"##bay-menu-{bay.Id}"))
        {
            if (ImGui.MenuItem(loaded == null ? "Insert"u8 : "Replace"u8))
            {
                _callbacks.InsertMedia(bay);
            }

            ImGui.BeginDisabled(loaded == null);
            if (ImGui.MenuItem("Eject"u8))
            {
                _callbacks.EjectMedia(bay.Id);
            }
            ImGui.EndDisabled();

            ImGui.EndPopup();
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
