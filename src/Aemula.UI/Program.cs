using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Hexa.NET.SDL3;
using Debugger = Aemula.Debugging.Debugger;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Aemula.UI;

public static unsafe class Program
{
    // A system swap disposes GPU resources the debugger windows hold, so it
    // has to run between frames on the main loop - never from an SDL event or
    // a file-dialog callback thread. The producer publishes one of these; the
    // loop consumes it with Interlocked.Exchange.
    private sealed class PendingLoad
    {
        public required SystemCatalogEntry Entry;
        public string? FilePath;
    }

    private static PendingLoad? _pendingLoad;

    // Rooted for the process lifetime so the GC can't collect the delegate
    // while a native file dialog still holds a pointer to it.
    private static readonly SDLDialogFileCallback RomDialogCallbackDelegate = RomDialogCallback;

    public static void Main(string[] args)
    {
        if (!SDL.Init((uint)(SDLInitFlags.Video | SDLInitFlags.Gamepad)))
        {
            Console.WriteLine($"Error: SDL_Init(): {SDL.GetErrorS()}");
            return;
        }

        var mainScale = SDL.GetDisplayContentScale(SDL.GetPrimaryDisplay());

        var emuWindowFlags = SDLWindowFlags.Resizable | SDLWindowFlags.Hidden | SDLWindowFlags.HighPixelDensity;
        var emuWindow = SDL.CreateWindow(
            "Aemula",
            (int)(1280 * mainScale),
            (int)(720 * mainScale),
            (ulong)emuWindowFlags);
        if (emuWindow.IsNull)
        {
            Console.WriteLine($"Error: SDL_CreateWindow(): {SDL.GetErrorS()}");
            return;
        }

        SDL.SetWindowPosition(emuWindow, (int)SDL.SDL_WINDOWPOS_CENTERED_MASK, (int)SDL.SDL_WINDOWPOS_CENTERED_MASK);
        SDL.ShowWindow(emuWindow);

        var gpuDevice = SDL.CreateGPUDevice(
            (uint)(SDLGPUShaderFormat.Spirv | SDLGPUShaderFormat.Dxil | SDLGPUShaderFormat.Metallib),
            true,
            (string?)null);
        if (gpuDevice.IsNull)
        {
            Console.WriteLine($"Error: SDL_CreateGPUDevice(): {SDL.GetErrorS()}");
            return;
        }

        // Both windows share this one GPU device - SDL_ClaimWindowForGPUDevice
        // is called once per window, here for the emulation window and inside
        // DebuggerHost for the debugger window.
        if (!SDL.ClaimWindowForGPUDevice(gpuDevice, emuWindow))
        {
            Console.WriteLine($"Error: SDL_ClaimWindowForGPUDevice(): {SDL.GetErrorS()}");
            return;
        }

        // Emulation window persists nothing (its layout is one forced
        // full-bleed window); the debugger window keeps the existing
        // imgui.ini.
        var emulationContext = new ImGuiWindowContext(gpuDevice, emuWindow, mainScale, iniFilename: null);

        var debuggerHost = new DebuggerHost(gpuDevice, mainScale);

        // --- system lifecycle ---
        EmulatedSystem? system = null;
        Debugger? debugger = null;
        var currentEntry = SystemCatalog.Default;
        var done = false;

        // Menu items fire while an ImGui frame is mid-build, so a callback must
        // never touch ImGui / GPU / system state directly (creating the
        // debugger context or swapping the system from inside BeginMenu leaves
        // a different context current when EndMenu runs). Callbacks only set
        // these; the loop drains them between frames.
        var pendingDebuggerToggle = false;
        var pendingReset = false;
        SystemCatalogEntry? pendingOpenRomEntry = null;

        // Perf cycle accounting. On the free-run path there's no Debugger to
        // hang a per-tick event off, so cycles come from
        // EmulatedSystem.TotalCycles; on the debugger-driven path
        // Debugger.RunForDuration ticks the system directly (TotalCycles
        // frozen) and Debugger.Ticked is what moves - summing the two is
        // correct because only one path runs per frame.
        var debuggerTickedCycles = 0UL;
        var lastTotalCycles = 0UL;
        var perfNominalMHz = 1.0;

        EmulationWindow emulationWindow = null!;

        void LoadSystem(SystemCatalogEntry entry, string? filePath)
        {
            system?.Dispose();

            var newSystem = entry.Create();
            newSystem.LoadProgram(filePath ?? "");
            var newDebugger = newSystem.CreateDebugger();
            if (newDebugger != null)
            {
                newDebugger.Ticked += () => debuggerTickedCycles++;
            }

            system = newSystem;
            debugger = newDebugger;
            currentEntry = entry;

            lastTotalCycles = 0;
            debuggerTickedCycles = 0;
            perfNominalMHz = newSystem.CyclesPerSecond / 1_000_000.0;

            emulationWindow.SetSystem(newSystem);
            debuggerHost.SetSystem(newSystem, newDebugger);
        }

        void ChooseSystem(SystemCatalogEntry entry)
        {
            if (entry.Rom == RomRequirement.Required)
            {
                // Pick the file first; only swap on a successful pick. Cancel
                // leaves the current system running.
                pendingOpenRomEntry = entry;
            }
            else
            {
                _pendingLoad = new PendingLoad { Entry = entry, FilePath = null };
            }
        }

        var callbacks = new EmulationWindow.Callbacks(
            CurrentEntry: () => currentEntry,
            ChooseSystem: ChooseSystem,
            OpenRom: () => pendingOpenRomEntry = currentEntry,
            ResetSystem: () => pendingReset = true,
            Quit: () => done = true,
            IsDebuggerVisible: () => debuggerHost.Visible,
            ToggleDebugger: () => pendingDebuggerToggle = true);

        emulationWindow = new EmulationWindow(gpuDevice, emulationContext, callbacks);

        // args are now an optional convenience: pre-select a system / file.
        {
            var startEntry = SystemCatalog.FindById(args.Length > 0 ? args[0] : null) ?? SystemCatalog.Default;
            var startFile = args.Length > 1 ? args[1] : null;
            if (startEntry.Rom == RomRequirement.Required && string.IsNullOrEmpty(startFile))
            {
                // Nothing to boot a Required-ROM system from - fall back to
                // the default (Apple II boots to BASIC unaided).
                startEntry = SystemCatalog.Default;
                startFile = null;
            }

            LoadSystem(startEntry, startFile);
        }

        var stopwatch = Stopwatch.StartNew();
        var lastTime = stopwatch.Elapsed;

        var perfWindowTime = TimeSpan.Zero;
        var perfWindowUpdateTime = TimeSpan.Zero;
        var perfWindowFrames = 0;
        var perfWindowCycles = 0UL;
        var perfFps = 0.0;
        var perfMsPerFrame = 0.0;
        var perfActualMHz = 0.0;

        var emulationClearColor = new Vector4(0f, 0f, 0f, 1f);
        var debuggerClearColor = new Vector4(0.45f, 0.55f, 0.60f, 1.00f);

        while (!done)
        {
            var elapsed = stopwatch.Elapsed;

            var realDeltaTimeSpan = elapsed - lastTime;
            var deltaTimeSpan = realDeltaTimeSpan;
            lastTime = elapsed;

            // TODO: Not right.
            if (deltaTimeSpan.TotalMilliseconds > 17)
            {
                deltaTimeSpan = TimeSpan.FromMilliseconds(17);
            }

            SDLEvent e = default;
            while (SDL.PollEvent(ref e))
            {
                var type = (SDLEventType)e.Type;

                // Backtick toggles the debugger window - swallowed entirely
                // (not routed to ImGui or the system) unless something is
                // taking text input. Rare enough as an emulated-machine key
                // that a plain binding is fine for v1; should become
                // rebindable / modifier-guarded if a supported system needs
                // the key.
                if (type == SDLEventType.KeyDown
                    && e.Key.Key == SDL.SDLK_GRAVE
                    && !emulationContext.WantTextInput
                    && !debuggerHost.WantTextInput)
                {
                    pendingDebuggerToggle = true;
                    continue;
                }

                if (type == SDLEventType.Quit)
                {
                    done = true;
                }
                else if (type == SDLEventType.WindowCloseRequested)
                {
                    if (e.Window.WindowID == emulationContext.WindowId)
                    {
                        done = true;
                    }
                    else if (e.Window.WindowID == debuggerHost.WindowId)
                    {
                        debuggerHost.Hide();
                    }
                }

                // Route to the owning window's ImGui context; windowless
                // events (gamepad, quit, device add/remove) go to both.
                if (TryGetEventWindowId(e, out var windowId))
                {
                    if (windowId == emulationContext.WindowId)
                    {
                        emulationContext.ProcessEvent(ref e);
                    }
                    else if (windowId == debuggerHost.WindowId && windowId != 0)
                    {
                        debuggerHost.ProcessEvent(ref e);
                    }
                }
                else
                {
                    emulationContext.ProcessEvent(ref e);
                    debuggerHost.ProcessEvent(ref e);
                }

                // Keyboard to the emulated system: always from the emulation
                // window; from the debugger window only when that context
                // isn't capturing the keyboard.
                if (type == SDLEventType.KeyDown || type == SDLEventType.KeyUp)
                {
                    var fromDebugger = debuggerHost.WindowId != 0 && e.Key.WindowID == debuggerHost.WindowId;
                    if (!fromDebugger || !debuggerHost.WantCaptureKeyboard)
                    {
                        emulationWindow.HandleKeyEvent(e.Key);
                    }
                }
            }

            // Drain deferred menu / hotkey actions now that no ImGui frame is
            // in flight - creating the debugger context or swapping the system
            // is only safe between frames.
            if (pendingDebuggerToggle)
            {
                pendingDebuggerToggle = false;
                debuggerHost.Toggle();
            }

            if (pendingReset)
            {
                pendingReset = false;
                system?.Reset();
            }

            if (pendingOpenRomEntry is { } romEntry)
            {
                pendingOpenRomEntry = null;
                ShowOpenRomDialog(emulationContext.Window, romEntry);
            }

            // Fold in any system swap requested from a menu or dialog callback.
            var pending = Interlocked.Exchange(ref _pendingLoad, null);
            if (pending != null)
            {
                LoadSystem(pending.Entry, pending.FilePath);
            }

            if (emulationContext.IsMinimized && !debuggerHost.Visible)
            {
                SDL.Delay(10);
                continue;
            }

            // Single tick driver - the system advances in exactly one place.
            // Neither window advances it itself.
            if (debuggerHost.Visible && debugger != null)
            {
                debugger.RunForDuration(deltaTimeSpan); // honours breakpoints / single-step / Stopped
            }
            else
            {
                system!.RunForDuration(deltaTimeSpan); // free-run
            }

            var emulatorTime = new EmulatorTime(elapsed, deltaTimeSpan);

            var emuCommandBuffer = SDL.AcquireGPUCommandBuffer(gpuDevice);
            emulationWindow.RenderFrame(emulatorTime, emuCommandBuffer, emulationClearColor);
            SDL.SubmitGPUCommandBuffer(emuCommandBuffer);

            if (debuggerHost.Visible)
            {
                debuggerHost.SetPerf(perfFps, perfMsPerFrame, perfActualMHz, perfNominalMHz);

                var debuggerCommandBuffer = SDL.AcquireGPUCommandBuffer(gpuDevice);
                debuggerHost.RenderFrame(emulatorTime, debuggerCommandBuffer, debuggerClearColor);
                SDL.SubmitGPUCommandBuffer(debuggerCommandBuffer);
            }

            var updateDuration = stopwatch.Elapsed - elapsed;

            var executedCycles = system!.TotalCycles - lastTotalCycles + debuggerTickedCycles;
            lastTotalCycles = system.TotalCycles;
            debuggerTickedCycles = 0;

            perfWindowTime += realDeltaTimeSpan;
            perfWindowUpdateTime += updateDuration;
            perfWindowFrames++;
            perfWindowCycles += executedCycles;
            if (perfWindowTime >= TimeSpan.FromSeconds(1))
            {
                perfFps = perfWindowFrames / perfWindowTime.TotalSeconds;
                perfMsPerFrame = perfWindowUpdateTime.TotalMilliseconds / perfWindowFrames;
                perfActualMHz = perfWindowCycles / perfWindowTime.TotalSeconds / 1_000_000.0;

                perfWindowTime = TimeSpan.Zero;
                perfWindowUpdateTime = TimeSpan.Zero;
                perfWindowFrames = 0;
                perfWindowCycles = 0;
            }
        }

        stopwatch.Stop();

        SDL.WaitForGPUIdle(gpuDevice);

        emulationWindow.Dispose();
        debuggerHost.Dispose();
        emulationContext.Dispose();
        system?.Dispose();

        SDL.ReleaseWindowFromGPUDevice(gpuDevice, emuWindow);
        SDL.DestroyGPUDevice(gpuDevice);
        SDL.DestroyWindow(emuWindow);
        SDL.Quit();
    }

    // The window an SDL event belongs to, for the events that carry one. The
    // first four fields (type, reserved, timestamp, windowID) share a layout
    // across every windowed event struct in the union, so Window.WindowID is
    // a valid read for all of them. Returns false for windowless events
    // (gamepad, quit, device hotplug), which are routed to both contexts.
    private static bool TryGetEventWindowId(in SDLEvent e, out uint windowId)
    {
        switch ((SDLEventType)e.Type)
        {
            case SDLEventType.KeyDown:
            case SDLEventType.KeyUp:
            case SDLEventType.TextInput:
            case SDLEventType.TextEditing:
            case SDLEventType.MouseMotion:
            case SDLEventType.MouseButtonDown:
            case SDLEventType.MouseButtonUp:
            case SDLEventType.MouseWheel:
            case SDLEventType.DropBegin:
            case SDLEventType.DropFile:
            case SDLEventType.DropText:
            case SDLEventType.DropComplete:
            case SDLEventType.DropPosition:
                windowId = e.Window.WindowID;
                return true;

            default:
                if (e.Type >= (uint)SDLEventType.WindowFirst && e.Type <= (uint)SDLEventType.WindowLast)
                {
                    windowId = e.Window.WindowID;
                    return true;
                }

                windowId = 0;
                return false;
        }
    }

    private sealed class RomDialogState
    {
        public required SystemCatalogEntry Entry;
        public unsafe SDLDialogFileFilter* NativeFilters;
        public int FilterCount;
        public GCHandle Self;
    }

    private static unsafe void ShowOpenRomDialog(SDLWindowPtr parent, SystemCatalogEntry entry)
    {
        if (entry.Rom == RomRequirement.None)
        {
            return;
        }

        var filters = entry.RomFilters;
        var count = filters.Length;

        var native = count > 0
            ? (SDLDialogFileFilter*)NativeMemory.Alloc((nuint)count, (nuint)sizeof(SDLDialogFileFilter))
            : null;
        for (var i = 0; i < count; i++)
        {
            native[i].Name = (byte*)Marshal.StringToCoTaskMemUTF8(filters[i].Name);
            native[i].Pattern = (byte*)Marshal.StringToCoTaskMemUTF8(filters[i].Pattern);
        }

        var state = new RomDialogState
        {
            Entry = entry,
            NativeFilters = native,
            FilterCount = count,
        };
        state.Self = GCHandle.Alloc(state);

        // Async: returns immediately, RomDialogCallback fires later (possibly
        // on another thread) and only publishes a PendingLoad. This SDL
        // binding's ShowOpenFileDialog takes no title argument - entry
        // .RomDialogTitle waits for a move to the properties-based API.
        SDL.ShowOpenFileDialog(
            RomDialogCallbackDelegate,
            (void*)GCHandle.ToIntPtr(state.Self),
            parent,
            native,
            count,
            (string?)null,
            false);
    }

    private static unsafe void RomDialogCallback(void* userdata, byte** filelist, int filter)
    {
        var handle = GCHandle.FromIntPtr((nint)userdata);
        var state = (RomDialogState)handle.Target!;

        try
        {
            // filelist == null -> error; filelist[0] == null -> cancelled.
            if (filelist != null && filelist[0] != null)
            {
                var path = Marshal.PtrToStringUTF8((nint)filelist[0]);
                if (!string.IsNullOrEmpty(path))
                {
                    Interlocked.Exchange(
                        ref _pendingLoad,
                        new PendingLoad { Entry = state.Entry, FilePath = path });
                }
            }
        }
        finally
        {
            for (var i = 0; i < state.FilterCount; i++)
            {
                Marshal.FreeCoTaskMem((nint)state.NativeFilters[i].Name);
                Marshal.FreeCoTaskMem((nint)state.NativeFilters[i].Pattern);
            }

            if (state.NativeFilters != null)
            {
                NativeMemory.Free(state.NativeFilters);
            }

            handle.Free();
        }
    }
}
