using System;
using System.Numerics;
using Hexa.NET.ImGui;
using Hexa.NET.SDL3;

namespace Aemula.UI;

// The default window: a full-bleed Television render with a menu bar, owning
// keyboard/gamepad input to the emulated system. No region overlays, no
// crosshair, no sidebar, no hover tooltip - deliberately just the picture.
// Audio output will live here later (see the note in RenderFrame).
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

    public EmulationWindow(SDLGPUDevicePtr gpuDevice, ImGuiWindowContext context, Callbacks callbacks)
    {
        _gpuDevice = gpuDevice;
        _context = context;
        _callbacks = callbacks;
    }

    public ImGuiWindowContext Context => _context;

    // Every system exposes EmulatedSystem.Television (concrete on the base
    // class), so this just grabs system.Television - no per-system branching.
    public void SetSystem(EmulatedSystem system)
    {
        _system = system;

        _textureView?.Dispose();
        _textureView = new TelevisionTextureView(system.Television);
        _textureView.CreateGraphicsResources(_gpuDevice);
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

        // Audio: a future SDL_OpenAudioDeviceStream is opened here and fed
        // from the same system tick that produces the samples. Not
        // implemented yet - this is the hook point.

        ImGui.Render();
        _context.Render(commandBuffer, clearColor);
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

            ImGui.EndMenu();
        }

        ImGui.EndMainMenuBar();
    }

    private void DrawPicture()
    {
        if (_textureView == null)
        {
            return;
        }

        // WorkPos/WorkSize already exclude the main menu bar, so a window
        // filling the work area sits neatly below it.
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.WorkPos);
        ImGui.SetNextWindowSize(viewport.WorkSize);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0f, 0f, 0f, 1f));

        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoDecoration
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoBringToFrontOnFocus
            | ImGuiWindowFlags.NoNavFocus
            | ImGuiWindowFlags.NoScrollWithMouse;

        if (ImGui.Begin("##emulation"u8, flags))
        {
            _textureView.DrawImage(activeVideoOnly: true);
        }

        ImGui.End();

        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }

    public void Dispose()
    {
        _textureView?.Dispose();
    }
}
