using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Aemula.Debugging;
using Hexa.NET.ImGui;
using Hexa.NET.SDL3;

namespace Aemula.UI;

// Everything the old Program.cs rendered: the dockspace, every DebuggerWindow
// (TelevisionWindow, DisassemblyWindow, LogicAnalyzerWindow, memory editors,
// ...), the perf readout, and the imgui.ini persistence - now its own OS
// window sharing the one SDL_GPUDevice. Created lazily on first Show(); hidden
// at startup and toggled with backtick / View ▸ Debugger afterwards. While
// hidden it isn't rendered and doesn't drive the clock (see Program's single
// tick driver).
public sealed class DebuggerHost : IDisposable
{
    private readonly SDLGPUDevicePtr _gpuDevice;
    private readonly float _mainScale;

    private ImGuiWindowContext? _context;
    private bool _visible;

    // One list instance for the host's lifetime - SetSystem clears and
    // repopulates it in place so the GCHandle the settings handler closes
    // over stays valid across a system swap.
    private readonly List<DebuggerWindow> _windows = [];
    private GCHandle _windowsHandle;

    private bool _firstRunLayout;

    // Perf readout - Program pushes the latest numbers in before each render.
    private double _perfFps;
    private double _perfMsPerFrame;
    private double _perfActualMHz;
    private double _perfNominalMHz;

    public DebuggerHost(SDLGPUDevicePtr gpuDevice, float mainScale)
    {
        _gpuDevice = gpuDevice;
        _mainScale = mainScale;
    }

    public bool Visible => _visible;

    public uint WindowId => _context?.WindowId ?? 0;

    public bool WantTextInput => _context?.WantTextInput ?? false;

    public bool WantCaptureKeyboard => _context?.WantCaptureKeyboard ?? false;

    public void SetPerf(double fps, double msPerFrame, double actualMHz, double nominalMHz)
    {
        _perfFps = fps;
        _perfMsPerFrame = msPerFrame;
        _perfActualMHz = actualMHz;
        _perfNominalMHz = nominalMHz;
    }

    public void Show()
    {
        EnsureInitialized();
        _visible = true;
        SDL.ShowWindow(_context!.Window);
        SDL.RaiseWindow(_context.Window);
    }

    public void Hide()
    {
        _visible = false;
        if (_context != null)
        {
            SDL.HideWindow(_context.Window);
        }
    }

    public void Toggle()
    {
        if (_visible)
        {
            Hide();
        }
        else
        {
            Show();
        }
    }

    private unsafe void EnsureInitialized()
    {
        if (_context != null)
        {
            return;
        }

        var windowFlags = SDLWindowFlags.Resizable | SDLWindowFlags.Hidden | SDLWindowFlags.HighPixelDensity;
        var window = SDL.CreateWindow(
            "Aemula — Debugger",
            (int)(1280 * _mainScale),
            (int)(720 * _mainScale),
            (ulong)windowFlags);
        if (window.IsNull)
        {
            throw new InvalidOperationException($"SDL_CreateWindow (debugger): {SDL.GetErrorS()}");
        }

        SDL.SetWindowPosition(window, (int)SDL.SDL_WINDOWPOS_CENTERED_MASK, (int)SDL.SDL_WINDOWPOS_CENTERED_MASK);

        if (!SDL.ClaimWindowForGPUDevice(_gpuDevice, window))
        {
            throw new InvalidOperationException($"SDL_ClaimWindowForGPUDevice (debugger): {SDL.GetErrorS()}");
        }

        _context = new ImGuiWindowContext(_gpuDevice, window, _mainScale, iniFilename: "imgui.ini");
        _context.MakeCurrent();

        _windowsHandle = GCHandle.Alloc(_windows);
        var typeNamePtr = (byte*)Marshal.StringToHGlobalAnsi("Aemula");
        var settingsHandler = new ImGuiSettingsHandler(
            typeName: typeNamePtr,
            typeHash: ImGuiP.ImHashStr(typeNamePtr),
            readOpenFn: &ImGuiSettingsReadOpen,
            readLineFn: &ImGuiSettingsReadLine,
            writeAllFn: &ImGuiSettingsWriteAll,
            userData: (void*)GCHandle.ToIntPtr(_windowsHandle));
        ImGuiP.AddSettingsHandler(ref settingsHandler);

        _firstRunLayout = !File.Exists("imgui.ini");

        // GPU resources for any windows SetSystem already built while the
        // host was still hidden and had no device to hand them.
        foreach (var debuggerWindow in _windows)
        {
            debuggerWindow.CreateGraphicsResources(_gpuDevice);
        }
    }

    public void SetSystem(EmulatedSystem system, Debugger? debugger)
    {
        foreach (var debuggerWindow in _windows)
        {
            debuggerWindow.Dispose();
        }
        _windows.Clear();

        debugger?.CreateDebuggerWindows(_windows);

        if (_context != null)
        {
            _context.MakeCurrent();
            foreach (var debuggerWindow in _windows)
            {
                debuggerWindow.CreateGraphicsResources(_gpuDevice);
            }

            // Force a fresh first-run dock layout for the new system's set of
            // windows.
            _firstRunLayout = true;
        }
    }

    public void ProcessEvent(ref SDLEvent e)
    {
        _context?.ProcessEvent(ref e);
    }

    public void RenderFrame(EmulatorTime time, SDLGPUCommandBufferPtr commandBuffer, Vector4 clearColor)
    {
        if (_context == null)
        {
            return;
        }

        _context.NewFrame();

        foreach (var debuggerWindow in _windows)
        {
            debuggerWindow.Prepare(time, commandBuffer);
        }

        DrawDockspace();
        DrawMainMenu();

        foreach (var debuggerWindow in _windows)
        {
            debuggerWindow.Draw(time);
        }

        ImGui.Render();
        _context.Render(commandBuffer, clearColor);
    }

    private unsafe void DrawDockspace()
    {
        const ImGuiDockNodeFlags dockSpaceFlags = ImGuiDockNodeFlags.None;

        var viewport = ImGui.GetMainViewport();
        var dockSpaceId = ImGui.DockSpaceOverViewport(viewport, dockSpaceFlags);

        if (!_firstRunLayout)
        {
            return;
        }

        _firstRunLayout = false;

        ImGuiP.DockBuilderRemoveNode(dockSpaceId);
        ImGuiP.DockBuilderAddNode(dockSpaceId, dockSpaceFlags | (ImGuiDockNodeFlags)ImGuiDockNodeFlagsPrivate.Space);
        ImGuiP.DockBuilderSetNodeSize(dockSpaceId, viewport.Size);

        uint outIdAtDir = 0;
        var dockIdLeft = ImGuiP.DockBuilderSplitNode(dockSpaceId, ImGuiDir.Left, 0.2f, &outIdAtDir, &dockSpaceId);
        var dockIdRight = ImGuiP.DockBuilderSplitNode(dockSpaceId, ImGuiDir.Right, 0.4f, &outIdAtDir, &dockSpaceId);
        var dockIdDown = ImGuiP.DockBuilderSplitNode(dockSpaceId, ImGuiDir.Down, 0.25f, &outIdAtDir, &dockSpaceId);

        foreach (var debuggerWindow in _windows)
        {
            uint? dockId = debuggerWindow.PreferredPane switch
            {
                Pane.Left => dockIdLeft,
                Pane.Bottom => dockIdDown,
                Pane.Right => dockIdRight,
                _ => null,
            };
            if (dockId != null)
            {
                debuggerWindow.IsOpen = true;
                ImGuiP.DockBuilderDockWindow($"{debuggerWindow.DisplayName}##{debuggerWindow.Name}", dockId.Value);
            }
        }

        ImGuiP.DockBuilderFinish(dockSpaceId);
    }

    private unsafe void DrawMainMenu()
    {
        if (!ImGui.BeginMainMenuBar())
        {
            return;
        }

        if (ImGui.BeginMenu("Windows"))
        {
            foreach (var debuggerWindow in _windows)
            {
                if (ImGui.MenuItem(debuggerWindow.DisplayName, (byte*)null, debuggerWindow.IsOpen, true))
                {
                    debuggerWindow.IsOpen = true;

                    ImGui.SetWindowFocus(debuggerWindow.Name);
                }
            }

            ImGui.EndMenu();
        }

        var perfText = $"{_perfFps:F0} FPS  {_perfMsPerFrame:F2} ms  {_perfActualMHz:F2} / {_perfNominalMHz:F2} MHz";
        var perfTextSize = ImGui.CalcTextSize(perfText);
        var perfTextX = ImGui.GetWindowWidth() - perfTextSize.X - ImGui.GetStyle().ItemSpacing.X;
        if (perfTextX > ImGui.GetCursorPosX())
        {
            ImGui.SetCursorPosX(perfTextX);
        }

        // Falling more than 5% behind the nominal clock is a sign that
        // debugger windows (e.g. TelevisionWindow, LogicAnalyzerWindow) are
        // too expensive to draw every frame and we're no longer keeping up
        // with real-time.
        if (_perfActualMHz < _perfNominalMHz * 0.95)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), perfText);
        }
        else
        {
            ImGui.TextUnformatted(perfText);
        }

        ImGui.EndMainMenuBar();
    }

    private static unsafe void* ImGuiSettingsReadOpen(ImGuiContext* context, ImGuiSettingsHandler* handler, byte* name)
    {
        var debuggerWindows = (List<DebuggerWindow>)GCHandle.FromIntPtr((nint)handler->UserData).Target!;

        var nameString = Marshal.PtrToStringAnsi((nint)name);

        foreach (var debuggerWindow in debuggerWindows)
        {
            if (debuggerWindow.Name == nameString)
            {
                return (void*)GCHandle.ToIntPtr(debuggerWindow.GCHandle);
            }
        }

        return null;
    }

    private static unsafe void ImGuiSettingsReadLine(ImGuiContext* context, ImGuiSettingsHandler* handler, void* entry, byte* line)
    {
        var debuggerWindow = (DebuggerWindow)GCHandle.FromIntPtr((nint)entry).Target!;

        var lineString = Marshal.PtrToStringAnsi((nint)line);
        if (lineString == null)
        {
            return;
        }

        debuggerWindow.ApplyPersistedSettingsLine(lineString);
    }

    private static unsafe void ImGuiSettingsWriteAll(ImGuiContext* context, ImGuiSettingsHandler* handler, ImGuiTextBuffer* buffer)
    {
        var debuggerWindows = (List<DebuggerWindow>)GCHandle.FromIntPtr((nint)handler->UserData).Target!;

        // [Aemula][Memory Editor #1]
        // IsOpen=1

        buffer->reserve(buffer->size() + debuggerWindows.Count * 32);
        foreach (var debuggerWindow in debuggerWindows)
        {
            buffer->append("["u8);
            buffer->append(handler->TypeName);
            buffer->append("]["u8);
            buffer->append(debuggerWindow.Name);
            buffer->append("]\n"u8);
            foreach (var line in debuggerWindow.GetPersistedSettingsLines())
            {
                buffer->append(line);
                buffer->append("\n"u8);
            }
        }
    }

    public void Dispose()
    {
        foreach (var debuggerWindow in _windows)
        {
            debuggerWindow.Dispose();
        }
        _windows.Clear();

        if (_context != null)
        {
            var window = _context.Window;
            _context.Dispose();
            SDL.ReleaseWindowFromGPUDevice(_gpuDevice, window);
            SDL.DestroyWindow(window);
            _context = null;
        }

        if (_windowsHandle.IsAllocated)
        {
            _windowsHandle.Free();
        }
    }
}
