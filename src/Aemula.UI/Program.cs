using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Aemula.Emulation.Systems.AppleII;
using Aemula.Emulation.Systems.Atari2600;
using Aemula.Emulation.Systems.Chip8;
using Aemula.Emulation.Systems.Nes;
using Aemula.Emulation.Systems.SpaceInvaders;
using Hexa.NET.ImGui;
using Hexa.NET.ImGui.Backends.SDL3;
using Hexa.NET.ImPlot;
using Hexa.NET.SDL3;

namespace Aemula.UI;

public static class Program
{
    private static readonly Dictionary<string, Func<EmulatedSystem>> Systems = new()
    {
        { "appleii", () => new AppleIISystem() },
        { "atari2600", () => new Atari2600System() },
        { "chip8", () => new Chip8System() },
        { "nes", () => new NesSystem() },
        { "spaceinvaders", () => new SpaceInvadersSystem() },
    };

    public static void Main(string[] args)
    {
        if (!SDL.Init((uint)(SDLInitFlags.Video | SDLInitFlags.Gamepad)))
        {
            Console.WriteLine($"Error: SDL_Init(): {SDL.GetErrorS()}");
            return;
        }

        var mainScale = SDL.GetDisplayContentScale(SDL.GetPrimaryDisplay());
        var windowFlags = SDLWindowFlags.Resizable | SDLWindowFlags.Hidden | SDLWindowFlags.HighPixelDensity;
        var window = SDL.CreateWindow(
            "Aemula",
            (int)(1280 * mainScale),
            (int)(720 * mainScale),
            (ulong)windowFlags);
        if (window.IsNull)
        {
            Console.WriteLine($"Error: SDL_CreateWindow(): {SDL.GetErrorS()}");
            return;
        }

        SDL.SetWindowPosition(window, (int)SDL.SDL_WINDOWPOS_CENTERED_MASK, (int)SDL.SDL_WINDOWPOS_CENTERED_MASK);
        SDL.ShowWindow(window);

        var gpuDevice = SDL.CreateGPUDevice(
            (uint)(SDLGPUShaderFormat.Spirv | SDLGPUShaderFormat.Dxil | SDLGPUShaderFormat.Metallib),
            true, 
            (string?)null);
        if (gpuDevice.IsNull)
        {
            Console.WriteLine($"Error: SDL_CreateGPUDevice(): {SDL.GetErrorS()}");
            return;
        }

        if (!SDL.ClaimWindowForGPUDevice(gpuDevice, window))
        {
            Console.WriteLine($"Error: SDL_ClaimWindowForGPUDevice(): {SDL.GetErrorS()}");
            return;
        }

        SDL.SetGPUSwapchainParameters(
            gpuDevice, 
            window,
            SDLGPUSwapchainComposition.Sdr, 
            SDLGPUPresentMode.Mailbox);

        var ctx = ImGui.CreateContext();
        ImGui.SetCurrentContext(ctx);

        var imPlotCtx = ImPlot.CreateContext();
        ImPlot.SetCurrentContext(imPlotCtx);
        ImPlot.SetImGuiContext(ctx);

        ImGuiIOPtr io = ImGui.GetIO();
        io.ConfigFlags |=
            ImGuiConfigFlags.NavEnableKeyboard
            | ImGuiConfigFlags.NavEnableGamepad
            | ImGuiConfigFlags.DockingEnable;
        // Without this, WantCaptureKeyboard is true any time a window has nav focus (i.e. almost
        // always), since ImGui itself wants keyboard for nav. That leaves no way for key events to
        // reach the emulated system.
        io.ConfigNavCaptureKeyboard = false;

        ImGui.StyleColorsDark();
        var style = ImGui.GetStyle();
        style.ScaleAllSizes(mainScale);
        style.FontScaleDpi = mainScale;
        io.ConfigDpiScaleFonts = true;
        io.ConfigDpiScaleViewports = true;

        ImGuiImplSDL3.SetCurrentContext(ctx);
        unsafe
        {
            ImGuiImplSDL3.InitForSDLGPU(
                new Hexa.NET.ImGui.Backends.SDL3.SDLWindowPtr(
                    (Hexa.NET.ImGui.Backends.SDL3.SDLWindow*)window.Handle));

            ImGuiImplSDLGPU3InitInfo initInfo = new(
                (Hexa.NET.ImGui.Backends.SDL3.SDLGPUDevice*)gpuDevice.Handle,
                colorTargetFormat: (int)SDL.GetGPUSwapchainTextureFormat(gpuDevice, window),
                msaaSamples: (int)SDLGPUSampleCount.Samplecount1);

            ImGuiImplSDL3.SDLGPU3Init(ref initInfo);
        }

        var stopwatch = new Stopwatch();
        stopwatch.Start();

        var lastTime = stopwatch.Elapsed;

        var systemArg = args[0];
        var system = Systems[systemArg]();

        var debugger = system.CreateDebugger();
        var debuggerWindows = new List<DebuggerWindow>();
        if (debugger != null)
        {
            debugger.CreateDebuggerWindows(debuggerWindows);
            foreach (var debuggerWindow in debuggerWindows)
            {
                debuggerWindow.CreateGraphicsResources(gpuDevice);
            }
        }

        unsafe
        {
            // We never free these, but that's okay, they're alive as long as this application is.
            var debuggerWindowsHandle = GCHandle.Alloc(debuggerWindows);
            var typeNamePtr = (byte*)Marshal.StringToHGlobalAnsi("Aemula");

            var settingsHandler = new ImGuiSettingsHandler(
                typeName: typeNamePtr,
                typeHash: ImGuiP.ImHashStr(typeNamePtr),
                readOpenFn: &ImGuiSettingsReadOpen,
                readLineFn: &ImGuiSettingsReadLine,
                writeAllFn: &ImGuiSettingsWriteAll,
                userData: (void*)GCHandle.ToIntPtr(debuggerWindowsHandle));
            ImGuiP.AddSettingsHandler(ref settingsHandler);
        }

        var programFilePath = args.Length > 1 ? args[1] : null;
        system.LoadProgram(programFilePath ?? "");

        Vector4 clearColor = new(0.45f, 0.55f, 0.60f, 1.00f);

        bool firstRun;
        unsafe
        {
            firstRun = !File.Exists(Marshal.PtrToStringAnsi((nint)ImGui.GetIO().IniFilename));
        }

        var done = false;
        while (!done)
        {
            var elapsed = stopwatch.Elapsed;

            var deltaTimeSpan = elapsed - lastTime;
            lastTime = elapsed;

            // TODO: Not right.
            if (deltaTimeSpan.TotalMilliseconds > 17)
            {
                deltaTimeSpan = TimeSpan.FromMilliseconds(17);
            }

            Hexa.NET.SDL3.SDLEvent e = default;
            while (SDL.PollEvent(ref e))
            {
                unsafe
                {
                    ImGuiImplSDL3.ProcessEvent((Hexa.NET.ImGui.Backends.SDL3.SDLEvent*)&e);
                }
                var type = (SDLEventType)e.Type;
                if (type == SDLEventType.Quit ||
                    (type == SDLEventType.WindowCloseRequested &&
                        e.Window.WindowID == SDL.GetWindowID(window)))
                {
                    done = true;
                }

                if (!ImGui.GetIO().WantCaptureKeyboard)
                {
                    if (type == SDLEventType.KeyDown || type == SDLEventType.KeyUp)
                    {
                        system.OnKeyEvent(e.Key);
                    }
                }
            }

            if (((SDLWindowFlags)SDL.GetWindowFlags(window) & SDLWindowFlags.Minimized) != 0)
            {
                SDL.Delay(10);
                continue;
            }

            ImGuiImplSDL3.SDLGPU3NewFrame();
            ImGuiImplSDL3.NewFrame();
            ImGui.NewFrame();
            
            var emulatorTime = new EmulatorTime(elapsed, deltaTimeSpan);

            var commandBuffer = SDL.AcquireGPUCommandBuffer(gpuDevice);

            foreach (var debuggerWindow in debuggerWindows)
            {
                debuggerWindow.Prepare(emulatorTime, commandBuffer);
            }

            debugger?.RunForDuration(deltaTimeSpan);

            DrawWindow(debuggerWindows, ref firstRun);
            DrawMainMenu(debuggerWindows);

            foreach (var debuggerWindow in debuggerWindows)
            {
                debuggerWindow.Draw(emulatorTime);
            }

            ImGui.Render();
            var drawData = ImGui.GetDrawData();
            bool isMinimized = drawData.DisplaySize.X <= 0 || drawData.DisplaySize.Y <= 0;

            unsafe
            {
                SDLGPUTexture* swapTexture;
                SDL.WaitAndAcquireGPUSwapchainTexture(commandBuffer, window, &swapTexture, null, null);

                if (swapTexture != null && !isMinimized)
                {
                    ImGuiImplSDL3.SDLGPU3PrepareDrawData(drawData, (Hexa.NET.ImGui.Backends.SDL3.SDLGPUCommandBuffer*)commandBuffer.Handle);

                    SDLGPUColorTargetInfo targetInfo = new()
                    {
                        Texture = swapTexture,
                        ClearColor = new SDLFColor
                        {
                            R = clearColor.X,
                            G = clearColor.Y,
                            B = clearColor.Z,
                            A = clearColor.W
                        },
                        LoadOp = SDLGPULoadOp.Clear,
                        StoreOp = SDLGPUStoreOp.Store,
                        MipLevel = 0,
                        LayerOrDepthPlane = 0,
                        Cycle = 0
                    };

                    var renderPass = SDL.BeginGPURenderPass(commandBuffer, &targetInfo, 1, null);
                    ImGuiImplSDL3.SDLGPU3RenderDrawData(
                        drawData,
                        (Hexa.NET.ImGui.Backends.SDL3.SDLGPUCommandBuffer*)commandBuffer.Handle,
                        (Hexa.NET.ImGui.Backends.SDL3.SDLGPURenderPass*)renderPass.Handle,
                        null);
                    SDL.EndGPURenderPass(renderPass);
                }

                if ((io.ConfigFlags & ImGuiConfigFlags.ViewportsEnable) != 0)
                {
                    ImGui.UpdatePlatformWindows();
                    ImGui.RenderPlatformWindowsDefault();
                }

                SDL.SubmitGPUCommandBuffer(commandBuffer);
            }
        }

        foreach (var debuggerWindow in debuggerWindows)
        {
            debuggerWindow.Dispose();
        }

        stopwatch.Stop();

        SDL.WaitForGPUIdle(gpuDevice);
        ImGuiImplSDL3.Shutdown();
        ImGuiImplSDL3.SDLGPU3Shutdown();
        ImPlot.DestroyContext();
        ImGui.DestroyContext();

        SDL.ReleaseWindowFromGPUDevice(gpuDevice, window);
        SDL.DestroyGPUDevice(gpuDevice);
        SDL.DestroyWindow(window);
        SDL.Quit();
    }

    private static unsafe void DrawWindow(List<DebuggerWindow> windows, ref bool firstRun)
    {
        const ImGuiDockNodeFlags dockSpaceFlags = ImGuiDockNodeFlags.None;

        var viewport = ImGui.GetMainViewport();
        var dockSpaceId = ImGui.DockSpaceOverViewport(viewport, dockSpaceFlags);

        if (firstRun)
        {
            firstRun = false;

            ImGuiP.DockBuilderRemoveNode(dockSpaceId);
            ImGuiP.DockBuilderAddNode(dockSpaceId, dockSpaceFlags | (ImGuiDockNodeFlags)ImGuiDockNodeFlagsPrivate.Space);
            ImGuiP.DockBuilderSetNodeSize(dockSpaceId, viewport.Size);

            uint outIdAtDir = 0;
            var dockIdLeft = ImGuiP.DockBuilderSplitNode(dockSpaceId, ImGuiDir.Left, 0.2f, &outIdAtDir, &dockSpaceId);
            var dockIdRight = ImGuiP.DockBuilderSplitNode(dockSpaceId, ImGuiDir.Right, 0.4f, &outIdAtDir, &dockSpaceId);
            var dockIdDown = ImGuiP.DockBuilderSplitNode(dockSpaceId, ImGuiDir.Down, 0.25f, &outIdAtDir, &dockSpaceId);

            foreach (var window in windows)
            {
                uint? dockId = window.PreferredPane switch
                {
                    Pane.Left => dockIdLeft,
                    Pane.Bottom => dockIdDown,
                    Pane.Right => dockIdRight,
                    _ => null,
                };
                if (dockId != null)
                {
                    window.IsOpen = true;
                    ImGuiP.DockBuilderDockWindow($"{window.DisplayName}##{window.Name}", dockId.Value);
                }
            }

            ImGuiP.DockBuilderFinish(dockSpaceId);
        }
    }

    private static unsafe void DrawMainMenu(List<DebuggerWindow> debuggerWindows)
    {
        if (ImGui.BeginMainMenuBar())
        {
            if (ImGui.BeginMenu("Windows"))
            {
                foreach (var debuggerWindow in debuggerWindows)
                {
                    if (ImGui.MenuItem(debuggerWindow.DisplayName, (byte*)null, debuggerWindow.IsOpen, true))
                    {
                        debuggerWindow.IsOpen = true;

                        ImGui.SetWindowFocus(debuggerWindow.Name);
                    }
                }

                ImGui.EndMenu();
            }

            ImGui.EndMainMenuBar();
        }
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

        if (lineString == "IsOpen=1")
        {
            debuggerWindow.IsOpen = true;
        }
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
            if (debuggerWindow.IsOpen)
            {
                buffer->append("IsOpen=1\n"u8);
            }
        }
    }
}
