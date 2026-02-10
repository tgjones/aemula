using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Aemula.Emulation.Systems.Atari2600;
using Aemula.Emulation.Systems.Chip8;
using Aemula.Emulation.Systems.Nes;
using Aemula.Emulation.Systems.SpaceInvaders;
using Hexa.NET.ImGui;
using Hexa.NET.ImGui.Backends.SDL3;
using Hexa.NET.SDL3;

namespace Aemula.UI;

public static class Program
{
    private static readonly Dictionary<string, Func<EmulatedSystem>> Systems = new()
    {
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
        ImGuiIOPtr io = ImGui.GetIO();
        io.ConfigFlags |=
            ImGuiConfigFlags.NavEnableKeyboard
            | ImGuiConfigFlags.NavEnableGamepad
            | ImGuiConfigFlags.DockingEnable;
            //| ImGuiConfigFlags.ViewportsEnable;

        ImGui.StyleColorsDark();
        var style = ImGui.GetStyle();
        style.ScaleAllSizes(mainScale);
        style.FontScaleDpi = mainScale;
        io.ConfigDpiScaleFonts = true;
        io.ConfigDpiScaleViewports = true;

        // TODO: Don't know if we need this.
        //if ((io.ConfigFlags & ImGuiConfigFlags.ViewportsEnable) != 0)
        //{
        //    style.WindowRounding = 0.0f;
        //    style.Colors[(int)ImGuiCol.WindowBg].W = 1.0f;
        //}

        Hexa.NET.ImGui.Backends.SDL3.ImGuiImplSDL3.SetCurrentContext(ctx);
        unsafe
        {
            Hexa.NET.ImGui.Backends.SDL3.ImGuiImplSDL3.InitForSDLGPU(
                new Hexa.NET.ImGui.Backends.SDL3.SDLWindowPtr(
                    (Hexa.NET.ImGui.Backends.SDL3.SDLWindow*)window.Handle));

            Hexa.NET.ImGui.Backends.SDL3.ImGuiImplSDLGPU3InitInfo initInfo = new()
            {
                Device = (Hexa.NET.ImGui.Backends.SDL3.SDLGPUDevice*)gpuDevice.Handle,
                ColorTargetFormat = (int)SDL.GetGPUSwapchainTextureFormat(gpuDevice, window),
                MSAASamples = (int)SDLGPUSampleCount.Samplecount1
            };
            Hexa.NET.ImGui.Backends.SDL3.ImGuiImplSDL3.SDLGPU3Init(ref initInfo);
        }

        var stopwatch = new Stopwatch();
        stopwatch.Start();

        var lastTime = stopwatch.Elapsed;

        var systemArg = args[0];
        var system = Systems[systemArg]();

        var debugger = system.CreateDebugger();
        DebuggerWindow[] debuggerWindows = [];
        if (debugger != null)
        {
            debuggerWindows = debugger.CreateDebuggerWindows().ToArray();
            foreach (var debuggerWindow in debuggerWindows)
            {
                debuggerWindow.CreateGraphicsResources(gpuDevice);
                debuggerWindow.IsVisible = true;
            }
        }

        system.LoadProgram(args[1]);

        Vector4 clearColor = new(0.45f, 0.55f, 0.60f, 1.00f);

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

            DrawWindow(debuggerWindows);
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
        ImGui.DestroyContext();

        SDL.ReleaseWindowFromGPUDevice(gpuDevice, window);
        SDL.DestroyGPUDevice(gpuDevice);
        SDL.DestroyWindow(window);
        SDL.Quit();
    }

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

    private static unsafe void DrawMainMenu(DebuggerWindow[] debuggerWindows)
    {
        if (ImGui.BeginMainMenuBar())
        {
            if (ImGui.BeginMenu("Windows"))
            {
                foreach (var debuggerWindow in debuggerWindows)
                {
                    if (ImGui.MenuItem(debuggerWindow.DisplayName, (byte*)null, debuggerWindow.IsVisible, true))
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
