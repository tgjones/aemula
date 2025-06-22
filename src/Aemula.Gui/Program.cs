using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Aemula.Core;
using Aemula.Core.UI;
using Aemula.Systems.Atari2600;
using Aemula.Systems.Chip8;
using Aemula.Systems.Nes;
using Aemula.Systems.SpaceInvaders;
using ImGuiNET;
using Veldrid;
using Veldrid.StartupUtilities;

namespace Aemula.Gui;

public static class Program
{
    private static readonly Dictionary<string, Func<EmulatedSystem>> Systems = new()
    {
        { "atari2600", () => new Atari2600() },
        { "chip8", () => new Chip8() },
        { "nes", () => new Nes() },
        { "spaceinvaders", () => new SpaceInvadersSystem() },
    };

    public static void Main(string[] args)
    {
        VeldridStartup.CreateWindowAndGraphicsDevice(
            new WindowCreateInfo(100, 100, 1024, 768, WindowState.Maximized, "Aemula"),
            out var window,
            out var graphicsDevice);

        graphicsDevice.SyncToVerticalBlank = true;

        var imGuiRenderer = new ImGuiRenderer(
            graphicsDevice,
            graphicsDevice.SwapchainFramebuffer.OutputDescription,
            (int)graphicsDevice.SwapchainFramebuffer.Width,
            (int)graphicsDevice.SwapchainFramebuffer.Height);

        var commandList = graphicsDevice.ResourceFactory.CreateCommandList();

        var windowOpen = true;

        window.Closed += () => windowOpen = false;
        window.Resized += () =>
        {
            imGuiRenderer.WindowResized(window.Width, window.Height);
            graphicsDevice.ResizeMainWindow((uint)window.Width, (uint)window.Height);
        };

        var stopwatch = new Stopwatch();
        stopwatch.Start();

        var lastTime = stopwatch.Elapsed;

        var systemArg = args[0];
        var system = Systems[systemArg]();

        var debugger = system.CreateDebugger();
        var debuggerWindows = debugger.CreateDebuggerWindows().ToArray();
        foreach (var debuggerWindow in debuggerWindows)
        {
            debuggerWindow.CreateGraphicsResources(graphicsDevice, imGuiRenderer);
            debuggerWindow.IsVisible = true;
        }

        system.LoadProgram(args[1]);

        ImGuiUtility.SetupDocking();

        while (windowOpen)
        {
            var elapsed = stopwatch.Elapsed;

            var deltaTimeSpan = elapsed - lastTime;
            lastTime = elapsed;

            // TODO: Not right.
            if (deltaTimeSpan.TotalMilliseconds > 17)
            {
                deltaTimeSpan = TimeSpan.FromMilliseconds(17);
            }

            var deltaTime = (float)deltaTimeSpan.TotalSeconds;
            var inputSnapshot = window.PumpEvents();

            imGuiRenderer.Update(deltaTime, inputSnapshot);

            foreach (var keyEvent in inputSnapshot.KeyEvents)
            {
                system.OnKeyEvent(keyEvent);
            }

            var emulatorTime = new EmulatorTime(elapsed, deltaTimeSpan);

            debugger.RunForDuration(deltaTimeSpan);

            DrawWindow(debuggerWindows);
            DrawMainMenu(debuggerWindows);

            foreach (var debuggerWindow in debuggerWindows)
            {
                debuggerWindow.Draw(emulatorTime);
            }

            commandList.Begin();
            commandList.SetFramebuffer(graphicsDevice.SwapchainFramebuffer);
            commandList.ClearColorTarget(0, RgbaFloat.Clear);

            imGuiRenderer.Render(graphicsDevice, commandList);

            commandList.End();

            graphicsDevice.SubmitCommands(commandList);
            graphicsDevice.SwapBuffers();
        }

        foreach (var debuggerWindow in debuggerWindows)
        {
            debuggerWindow.Dispose();
        }

        stopwatch.Stop();

        commandList.Dispose();
        imGuiRenderer.Dispose();
        graphicsDevice.Dispose();
    }

    private static bool _firstTime = true;

    private static unsafe void DrawWindow(DebuggerWindow[] windows)
    {
        const ImGuiDockNodeFlags dockSpaceFlags = ImGuiDockNodeFlags.None;

        var viewport = ImGui.GetMainViewport();
        var dockSpaceId = ImGui.DockSpaceOverViewport(viewport, dockSpaceFlags);

        //if (_firstTime)
        //{
        //    _firstTime = false;

        //    ImGuiExtra.DockBuilderRemoveNode(dockSpaceId);
        //    ImGuiExtra.DockBuilderAddNode(dockSpaceId, dockSpaceFlags | ImGuiExtra.ImGuiDockNodeFlags_DockSpace);
        //    ImGuiExtra.DockBuilderSetNodeSize(dockSpaceId, viewport.Size);

        //    var dockIdLeft = ImGuiExtra.DockBuilderSplitNode(dockSpaceId, ImGuiDir.Left, 0.2f, out _, out dockSpaceId);
        //    var dockIdRight = ImGuiExtra.DockBuilderSplitNode(dockSpaceId, ImGuiDir.Right, 0.4f, out _, out dockSpaceId);
        //    var dockIdDown = ImGuiExtra.DockBuilderSplitNode(dockSpaceId, ImGuiDir.Down, 0.25f, out _, out dockSpaceId);

        //    foreach (var window in windows)
        //    {
        //        uint? dockId = window.PreferredPane switch
        //        {
        //            Pane.Left => dockIdLeft,
        //            Pane.Bottom => dockIdDown,
        //            Pane.Right => dockIdRight,
        //            _ => null,
        //        };
        //        if (dockId != null)
        //        {
        //            ImGuiExtra.DockBuilderDockWindow($"{window.DisplayName}##{window.Name}", dockId.Value);
        //        }
        //    }

        //    ImGuiExtra.DockBuilderFinish(dockSpaceId);
        //}

        //ImGui.End();
    }

    private static void DrawMainMenu(DebuggerWindow[] debuggerWindows)
    {
        if (ImGui.BeginMainMenuBar())
        {
            if (ImGui.BeginMenu("Windows"))
            {
                foreach (var debuggerWindow in debuggerWindows)
                {
                    if (ImGui.MenuItem(debuggerWindow.DisplayName, null, debuggerWindow.IsVisible, true))
                    {
                        debuggerWindow.IsVisible = true;

                        ImGui.SetWindowFocus(debuggerWindow.Name);
                    }
                }

                ImGui.EndMenu();
            }

            ImGui.EndMainMenuBar();
        }
    }
}

public static class ImGuiExtra
{
    public static unsafe void DockBuilderDockWindow(string window_name, uint node_id)
    {
        var windowNameByteCount = Encoding.UTF8.GetByteCount(window_name);
        byte* windowNamePtr;
        if (windowNameByteCount > 2048)
        {
            windowNamePtr = (byte*)Marshal.AllocHGlobal(windowNameByteCount);
        }
        else
        {
            byte* stackPtr = stackalloc byte[windowNameByteCount + 1];
            windowNamePtr = stackPtr;
        }

        var windowNameSpan = new Span<byte>(windowNamePtr, windowNameByteCount + 1);
        Encoding.UTF8.GetBytes(window_name, windowNameSpan);

        ImGuiNativeExtra.igDockBuilderDockWindow(windowNamePtr, node_id);

        if (windowNameByteCount > 2048)
        {
            Marshal.FreeHGlobal((IntPtr)windowNamePtr);
        }
    }

    public static uint DockBuilderAddNode(uint id, ImGuiDockNodeFlags flags)
    {
        return ImGuiNativeExtra.igDockBuilderAddNode(id, flags);
    }

    public static void DockBuilderRemoveNode(uint id)
    {
        ImGuiNativeExtra.igDockBuilderRemoveNode(id);
    }

    public static void DockBuilderSetNodeSize(uint id, Vector2 size)
    {
        ImGuiNativeExtra.igDockBuilderSetNodeSize(id, size);
    }

    public static unsafe uint DockBuilderSplitNode(uint nodeId, ImGuiDir split_dir, float size_ratio_for_node_at_dir, out uint out_id_at_dir, out uint out_id_at_opposite_dir)
    {
        fixed (uint* out_id_at_dir_ptr = &out_id_at_dir)
        fixed (uint* out_id_at_opposite_dir_ptr = &out_id_at_opposite_dir)
        {
            return ImGuiNativeExtra.igDockBuilderSplitNode(nodeId, split_dir, size_ratio_for_node_at_dir, out_id_at_dir_ptr, out_id_at_opposite_dir_ptr);
        }
    }

    public static void DockBuilderFinish(uint id)
    {
        ImGuiNativeExtra.igDockBuilderFinish(id);
    }

    public const ImGuiDockNodeFlags ImGuiDockNodeFlags_DockSpace = (ImGuiDockNodeFlags)(1 << 10);
}

internal static class ImGuiNativeExtra
{
    [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
    public unsafe static extern void igDockBuilderDockWindow(byte* window_name, uint node_id);

    [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
    public unsafe static extern uint igDockBuilderAddNode(uint id, ImGuiDockNodeFlags flags);

    [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
    public unsafe static extern void igDockBuilderRemoveNode(uint id);

    [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
    public unsafe static extern void igDockBuilderSetNodeSize(uint id, Vector2 size);

    [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
    public unsafe static extern uint igDockBuilderSplitNode(uint node_id, ImGuiDir split_dir, float size_ratio_for_node_at_dir, uint* out_id_at_dir, uint* out_id_at_opposite_dir);

    [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
    public unsafe static extern void igDockBuilderFinish(uint id);
}
