using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Hexa.NET.ImGui;
using Hexa.NET.ImPlot;
using Hexa.NET.SDL3;
using ImGuiImplSDL3 = Hexa.NET.ImGui.Backends.SDL3.ImGuiImplSDL3;
using ImGuiImplSDLGPU3InitInfo = Hexa.NET.ImGui.Backends.SDL3.ImGuiImplSDLGPU3InitInfo;
using BackendSDLWindow = Hexa.NET.ImGui.Backends.SDL3.SDLWindow;
using BackendSDLWindowPtr = Hexa.NET.ImGui.Backends.SDL3.SDLWindowPtr;
using BackendGPUDevice = Hexa.NET.ImGui.Backends.SDL3.SDLGPUDevice;
using BackendCommandBuffer = Hexa.NET.ImGui.Backends.SDL3.SDLGPUCommandBuffer;
using BackendRenderPass = Hexa.NET.ImGui.Backends.SDL3.SDLGPURenderPass;
using BackendEvent = Hexa.NET.ImGui.Backends.SDL3.SDLEvent;

namespace Aemula.UI;

// One Dear ImGui context bound to one OS window, with its own SDL3 + SDLGPU3
// backend init and its own ImPlot context - the "one context per OS window"
// pattern the emulation/debugger window split needs. Both windows share the
// single SDL_GPUDevice (passed in), so textures and the upload paths don't
// need duplicating; only the ImGui-side state is per window.
//
// Every method that touches ImGui/ImPlot/backend state calls MakeCurrent()
// first, because the Hexa.NET backends key their state off the current
// context. This is the first place the codebase stands up two live ImGui +
// SDLGPU backend instances at once.
public sealed class ImGuiWindowContext : IDisposable
{
    private readonly SDLGPUDevicePtr _gpuDevice;
    private readonly SDLWindowPtr _window;
    private readonly ImGuiContextPtr _imGuiContext;
    private readonly ImPlotContextPtr _imPlotContext;
    private nint _iniFilenamePtr;
    private bool _disposed;

    public SDLWindowPtr Window => _window;
    public uint WindowId => SDL.GetWindowID(_window);

    public unsafe ImGuiWindowContext(SDLGPUDevicePtr gpuDevice, SDLWindowPtr window, float mainScale, string? iniFilename)
    {
        _gpuDevice = gpuDevice;
        _window = window;

        SDL.SetGPUSwapchainParameters(
            gpuDevice,
            window,
            SDLGPUSwapchainComposition.Sdr,
            SDLGPUPresentMode.Mailbox);

        _imGuiContext = ImGui.CreateContext();
        ImGui.SetCurrentContext(_imGuiContext);

        _imPlotContext = ImPlot.CreateContext();
        ImPlot.SetCurrentContext(_imPlotContext);
        ImPlot.SetImGuiContext(_imGuiContext);

        var io = ImGui.GetIO();
        io.ConfigFlags |=
            ImGuiConfigFlags.NavEnableKeyboard
            | ImGuiConfigFlags.NavEnableGamepad
            | ImGuiConfigFlags.DockingEnable;
        // Without this, WantCaptureKeyboard is true any time a window has nav
        // focus (i.e. almost always), since ImGui itself wants keyboard for
        // nav - leaving no way for key events to reach the emulated system.
        io.ConfigNavCaptureKeyboard = false;

        if (iniFilename == null)
        {
            io.IniFilename = null;
        }
        else
        {
            _iniFilenamePtr = Marshal.StringToHGlobalAnsi(iniFilename);
            io.IniFilename = (byte*)_iniFilenamePtr;
        }

        ImGui.StyleColorsDark();
        var style = ImGui.GetStyle();
        style.ScaleAllSizes(mainScale);
        style.FontScaleDpi = mainScale;
        io.ConfigDpiScaleFonts = true;
        io.ConfigDpiScaleViewports = true;

        ImGuiImplSDL3.SetCurrentContext(_imGuiContext);
        ImGuiImplSDL3.InitForSDLGPU(new BackendSDLWindowPtr((BackendSDLWindow*)window.Handle));

        ImGuiImplSDLGPU3InitInfo initInfo = new(
            (BackendGPUDevice*)gpuDevice.Handle,
            colorTargetFormat: (int)SDL.GetGPUSwapchainTextureFormat(gpuDevice, window),
            msaaSamples: (int)SDLGPUSampleCount.Samplecount1);
        ImGuiImplSDL3.SDLGPU3Init(ref initInfo);
    }

    public void MakeCurrent()
    {
        ImGui.SetCurrentContext(_imGuiContext);
        ImGuiImplSDL3.SetCurrentContext(_imGuiContext);
        ImPlot.SetCurrentContext(_imPlotContext);
        ImPlot.SetImGuiContext(_imGuiContext);
    }

    public bool WantCaptureKeyboard
    {
        get
        {
            MakeCurrent();
            return ImGui.GetIO().WantCaptureKeyboard;
        }
    }

    public bool WantTextInput
    {
        get
        {
            MakeCurrent();
            return ImGui.GetIO().WantTextInput;
        }
    }

    public bool IsMinimized =>
        ((SDLWindowFlags)SDL.GetWindowFlags(_window) & SDLWindowFlags.Minimized) != 0;

    public unsafe void ProcessEvent(ref SDLEvent e)
    {
        MakeCurrent();
        fixed (SDLEvent* ptr = &e)
        {
            ImGuiImplSDL3.ProcessEvent((BackendEvent*)ptr);
        }
    }

    public void NewFrame()
    {
        MakeCurrent();
        ImGuiImplSDL3.SDLGPU3NewFrame();
        ImGuiImplSDL3.NewFrame();
        ImGui.NewFrame();
    }

    // Renders this context's already-built draw data (NewFrame + UI +
    // ImGui.Render must have happened) into the window's swapchain within the
    // given command buffer, clearing to clearColor. A no-op for the frame if
    // the swapchain image isn't available (e.g. minimized).
    public unsafe void Render(SDLGPUCommandBufferPtr commandBuffer, Vector4 clearColor)
    {
        MakeCurrent();

        var drawData = ImGui.GetDrawData();
        var isMinimized = drawData.DisplaySize.X <= 0 || drawData.DisplaySize.Y <= 0;

        SDLGPUTexture* swapTexture;
        SDL.WaitAndAcquireGPUSwapchainTexture(commandBuffer, _window, &swapTexture, null, null);

        if (swapTexture == null || isMinimized)
        {
            return;
        }

        ImGuiImplSDL3.SDLGPU3PrepareDrawData(drawData, (BackendCommandBuffer*)commandBuffer.Handle);

        SDLGPUColorTargetInfo targetInfo = new()
        {
            Texture = swapTexture,
            ClearColor = new SDLFColor
            {
                R = clearColor.X,
                G = clearColor.Y,
                B = clearColor.Z,
                A = clearColor.W,
            },
            LoadOp = SDLGPULoadOp.Clear,
            StoreOp = SDLGPUStoreOp.Store,
            MipLevel = 0,
            LayerOrDepthPlane = 0,
            Cycle = 0,
        };

        var renderPass = SDL.BeginGPURenderPass(commandBuffer, &targetInfo, 1, null);
        ImGuiImplSDL3.SDLGPU3RenderDrawData(
            drawData,
            (BackendCommandBuffer*)commandBuffer.Handle,
            (BackendRenderPass*)renderPass.Handle,
            null);
        SDL.EndGPURenderPass(renderPass);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        MakeCurrent();
        ImGuiImplSDL3.Shutdown();
        ImGuiImplSDL3.SDLGPU3Shutdown();
        ImPlot.DestroyContext(_imPlotContext);
        ImGui.DestroyContext(_imGuiContext);

        if (_iniFilenamePtr != 0)
        {
            Marshal.FreeHGlobal(_iniFilenamePtr);
            _iniFilenamePtr = 0;
        }
    }
}
