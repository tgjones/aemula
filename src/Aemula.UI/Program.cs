using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Aemula;
using Aemula.Emulation.Systems;
using Hexa.NET.SDL3;
using Debugger = Aemula.Debugging.Debugger;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Aemula.UI;

public static unsafe class Program
{
    // A system swap - or a card change, or a Hard Reset - disposes GPU
    // resources the debugger windows hold, so it has to run between frames on
    // the main loop, never from an SDL event or a file-dialog callback thread.
    // The producer publishes one of these with the descriptor to (re)build;
    // the loop consumes it with Interlocked.Exchange.
    private sealed class PendingLoad
    {
        public required SystemDescriptor Descriptor;
    }

    // A media file the user picked in the open dialog, published from the
    // (possibly off-thread) dialog callback and drained between frames.
    private sealed class PendingMedia
    {
        public required string BayId;
        public required string Path;
    }

    private static PendingLoad? _pendingLoad;
    private static PendingMedia? _pendingMediaInsert;

    // Rooted for the process lifetime so the GC can't collect the delegate
    // while a native file dialog still holds a pointer to it.
    private static readonly SDLDialogFileCallback MediaDialogCallbackDelegate = MediaDialogCallback;

    public static void Main(string[] args)
    {
        // Audio is here for the playback device the emulation window opens per
        // system - without SDLInitFlags.Audio the SDL audio subsystem is not
        // brought up and SDL_OpenAudioDeviceStream fails.
        if (!SDL.Init((uint)(SDLInitFlags.Video | SDLInitFlags.Gamepad | SDLInitFlags.Audio)))
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
        Rig? rig = null;
        Debugger? debugger = null;
        var currentDescriptor = EmulatedSystems.All[0];
        var done = false;

        // The user's expansion-card picks for the current system: slot id ->
        // card id, with a null value meaning a deliberately empty slot and an
        // absent key meaning "take the slot's default". Kept across rebuilds of
        // the same machine; cleared when the system changes.
        var slotChoices = new Dictionary<string, string?>();

        // The media the user has loaded into the current machine: bay id ->
        // image. Re-applied to the freshly built Rig after every rebuild
        // (system swap, card change, Hard Reset). Cleared when the system
        // changes.
        var mediaImages = new Dictionary<string, MediaImage>();

        ExpansionSlotConfiguration BuildSlotConfiguration() =>
            new(slotChoices.Select(choice => (choice.Key, choice.Value)));

        // Menu items fire while an ImGui frame is mid-build, so a callback must
        // never touch ImGui / GPU / system state directly (creating the
        // debugger context or swapping the system from inside BeginMenu leaves
        // a different context current when EndMenu runs). Callbacks only set
        // these; the loop drains them between frames.
        var pendingDebuggerToggle = false;
        var pendingSoftReset = false;
        var pendingHardReset = false;
        MediaBay? pendingMediaDialog = null;
        string? pendingMediaEject = null;

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

        void LoadSystem(SystemDescriptor descriptor)
        {
            // A different machine starts from its own slot defaults and with no
            // media loaded.
            if (descriptor.Id != currentDescriptor.Id)
            {
                slotChoices.Clear();
                mediaImages.Clear();
            }

            rig?.Dispose();

            var newRig = descriptor.Build(BuildSlotConfiguration());

            // A rebuild makes a fresh Rig with empty bays - re-seat whatever
            // the user had loaded. A bay that no longer exists (its card was
            // removed) just drops its image.
            foreach (var (bayId, image) in mediaImages.ToArray())
            {
                try
                {
                    newRig.InsertMedia(bayId, image);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: could not re-insert media in '{bayId}': {ex.Message}");
                    mediaImages.Remove(bayId);
                }
            }

            var newDebugger = newRig.System.CreateDebugger();
            if (newDebugger != null)
            {
                newDebugger.Ticked += () => debuggerTickedCycles++;
            }

            rig = newRig;
            debugger = newDebugger;
            currentDescriptor = descriptor;

            lastTotalCycles = 0;
            debuggerTickedCycles = 0;
            perfNominalMHz = newRig.System.CyclesPerSecond / 1_000_000.0;

            emulationWindow.SetRig(newRig);
            debuggerHost.SetSystem(newRig.System, newDebugger);

            // If the new machine needs a cartridge and hasn't got one, open the
            // picker straight away rather than sit on a black screen.
            var requiredEmpty = newRig.MediaBays.FirstOrDefault(
                bay => bay.Required && !mediaImages.ContainsKey(bay.Id));
            if (requiredEmpty != null)
            {
                pendingMediaDialog = requiredEmpty;
            }
        }

        void ChooseSystem(SystemDescriptor descriptor)
        {
            _pendingLoad = new PendingLoad { Descriptor = descriptor };
        }

        // The card fitted in slotId right now: an explicit pick if the user made
        // one, otherwise the slot's default.
        string? SelectedSlotCard(string slotId) =>
            slotChoices.TryGetValue(slotId, out var chosen)
                ? chosen
                : currentDescriptor.Slots.FirstOrDefault(slot => slot.Id == slotId)?.DefaultCardId;

        void ChooseSlotCard(string slotId, string? cardId)
        {
            slotChoices[slotId] = cardId;

            // Changing a card is a machine rebuild, same as a system swap - and
            // like one, it has to run between frames, so just request it.
            _pendingLoad = new PendingLoad { Descriptor = currentDescriptor };
        }

        var callbacks = new EmulationWindow.Callbacks(
            CurrentSystem: () => currentDescriptor,
            ChooseSystem: ChooseSystem,
            SoftReset: () => pendingSoftReset = true,
            HardReset: () => pendingHardReset = true,
            Quit: () => done = true,
            IsDebuggerVisible: () => debuggerHost.Visible,
            ToggleDebugger: () => pendingDebuggerToggle = true,
            SelectedSlotCard: SelectedSlotCard,
            ChooseSlotCard: ChooseSlotCard,
            MediaBays: () => rig?.MediaBays ?? [],
            BayHasMedia: bayId => mediaImages.ContainsKey(bayId),
            InsertMedia: bay => pendingMediaDialog = bay,
            EjectMedia: bayId => pendingMediaEject = bayId);

        emulationWindow = new EmulationWindow(gpuDevice, emulationContext, callbacks);

        // args are an optional convenience: pre-select a system, and (arg 2) a
        // media file for its first bay.
        {
            var startDescriptor = EmulatedSystems.FindById(args.Length > 0 ? args[0] : null) ?? EmulatedSystems.All[0];
            LoadSystem(startDescriptor);

            if (args.Length > 1 && rig!.MediaBays.Count > 0)
            {
                try
                {
                    var image = MediaImage.FromFile(args[1]);
                    var bayId = rig.MediaBays[0].Id;
                    rig.InsertMedia(bayId, image);
                    mediaImages[bayId] = image;
                    pendingMediaDialog = null;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Could not load '{args[1]}': {ex.Message}");
                }
            }
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

            if (pendingSoftReset)
            {
                pendingSoftReset = false;
                rig?.Reset();
            }

            if (pendingHardReset)
            {
                pendingHardReset = false;
                // Rebuild the current machine from cold with the same slots and
                // the same inserted media - the only clean way out of a wedged
                // machine, and the way to change a cartridge cleanly.
                _pendingLoad = new PendingLoad { Descriptor = currentDescriptor };
            }

            if (pendingMediaDialog is { } dialogBay)
            {
                pendingMediaDialog = null;
                ShowMediaDialog(emulationContext.Window, dialogBay);
            }

            if (pendingMediaEject is { } ejectBayId)
            {
                pendingMediaEject = null;
                try
                {
                    rig?.EjectMedia(ejectBayId);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Could not eject '{ejectBayId}': {ex.Message}");
                }
                mediaImages.Remove(ejectBayId);
            }

            // A media file picked from the open dialog: load it in place,
            // leaving the running CPU (and whatever's already typed at the
            // prompt) untouched. A truncated / invalid file shows a message
            // rather than crashing.
            var mediaInsert = Interlocked.Exchange(ref _pendingMediaInsert, null);
            if (mediaInsert != null && rig != null)
            {
                try
                {
                    var image = MediaImage.FromFile(mediaInsert.Path);
                    rig.InsertMedia(mediaInsert.BayId, image);
                    mediaImages[mediaInsert.BayId] = image;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Could not load media for '{mediaInsert.BayId}': {ex.Message}");
                }
            }

            // Fold in any system swap / rebuild requested from a menu or hotkey.
            var pending = Interlocked.Exchange(ref _pendingLoad, null);
            if (pending != null)
            {
                LoadSystem(pending.Descriptor);
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
                rig!.RunForDuration(deltaTimeSpan); // free-run
            }

            var emulatorTime = new EmulatorTime(elapsed, deltaTimeSpan);

            emulationWindow.SetPerf(perfFps, perfMsPerFrame, perfActualMHz, perfNominalMHz);

            var emuCommandBuffer = SDL.AcquireGPUCommandBuffer(gpuDevice);
            emulationWindow.RenderFrame(emulatorTime, emuCommandBuffer, emulationClearColor);
            SDL.SubmitGPUCommandBuffer(emuCommandBuffer);

            if (debuggerHost.Visible)
            {
                var debuggerCommandBuffer = SDL.AcquireGPUCommandBuffer(gpuDevice);
                debuggerHost.RenderFrame(emulatorTime, debuggerCommandBuffer, debuggerClearColor);
                SDL.SubmitGPUCommandBuffer(debuggerCommandBuffer);
            }

            var updateDuration = stopwatch.Elapsed - elapsed;

            var executedCycles = rig!.System.TotalCycles - lastTotalCycles + debuggerTickedCycles;
            lastTotalCycles = rig.System.TotalCycles;
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
        rig?.Dispose();

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

    private sealed class MediaDialogState
    {
        public required string BayId;
        public unsafe SDLDialogFileFilter* NativeFilters;
        public int FilterCount;
        public GCHandle Self;
    }

    private static unsafe void ShowMediaDialog(SDLWindowPtr parent, MediaBay bay)
    {
        var filters = bay.Filters;
        var count = filters.Count;

        var native = count > 0
            ? (SDLDialogFileFilter*)NativeMemory.Alloc((nuint)count, (nuint)sizeof(SDLDialogFileFilter))
            : null;
        for (var i = 0; i < count; i++)
        {
            native[i].Name = (byte*)Marshal.StringToCoTaskMemUTF8(filters[i].Name);
            native[i].Pattern = (byte*)Marshal.StringToCoTaskMemUTF8(filters[i].Pattern);
        }

        var state = new MediaDialogState
        {
            BayId = bay.Id,
            NativeFilters = native,
            FilterCount = count,
        };
        state.Self = GCHandle.Alloc(state);

        // Async: returns immediately, MediaDialogCallback fires later (possibly
        // on another thread) and only publishes a PendingMedia. This SDL
        // binding's ShowOpenFileDialog takes no title argument - bay
        // .DialogTitle waits for a move to the properties-based API.
        SDL.ShowOpenFileDialog(
            MediaDialogCallbackDelegate,
            (void*)GCHandle.ToIntPtr(state.Self),
            parent,
            native,
            count,
            (string?)null,
            false);
    }

    private static unsafe void MediaDialogCallback(void* userdata, byte** filelist, int filter)
    {
        var handle = GCHandle.FromIntPtr((nint)userdata);
        var state = (MediaDialogState)handle.Target!;

        try
        {
            // filelist == null -> error; filelist[0] == null -> cancelled.
            if (filelist != null && filelist[0] != null)
            {
                var path = Marshal.PtrToStringUTF8((nint)filelist[0]);
                if (!string.IsNullOrEmpty(path))
                {
                    Interlocked.Exchange(
                        ref _pendingMediaInsert,
                        new PendingMedia { BayId = state.BayId, Path = path });
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
