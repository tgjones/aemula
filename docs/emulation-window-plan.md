# Emulation window / debugger window split — Implementation Plan

## Goal

Split `Aemula.UI` into two OS windows:

- **Emulation window** — the default window. A borderless-feeling view of the
  running machine: renders a `Television` full-bleed (no overlays, no sidebar,
  no crosshair), owns keyboard/gamepad input to the emulated system, and is
  where audio output will live later. Carries a menu bar whose **File** menu
  picks the system and, where the system supports one, a ROM / program file
  (and later a disk image).

- **Debugger window** — everything `Program.cs` renders today: the dockspace,
  every `DebuggerWindow` (`TelevisionWindow`, `DisassemblyWindow`,
  `LogicAnalyzerWindow`, memory editors, …), the perf readout, and the ini
  persistence. Hidden at startup. Toggled with the **backtick** (`` ` ``) key,
  and from a **View ▸ Debugger** menu item in the emulation window.

Today `Program.cs` takes the system name (and optional file) as `args[0]`/
`args[1]`, builds one SDL window that *is* the debugger, and starts with the
debugger `Stopped` so nothing runs until the user hits play. After this change
the emulation window comes up running a default system immediately, and args
become an optional convenience (pre-select a system / file) rather than the
only way in.

## Design decision: two SDL windows, one GPU device, two ImGui contexts

Both windows are real `SDL_Window`s. They **share one `SDL_GPUDevice`** (created
once, `SDL_ClaimWindowForGPUDevice` called for each window) so textures and the
`TelevisionWindow`/emulation-view upload paths don't need duplicating.

Each window gets **its own ImGui context** (`ImGui.CreateContext()` ×2), each
with its own SDL3 + SDLGPU3 backend init, its own `io.IniFilename`, and its own
`ImPlot` context association. This is the standard Dear ImGui "one context per
OS window" pattern. Per-frame:

1. Poll SDL events **once**. Route each event to a context by `e.window.windowID`
   (`ImGuiImplSDL3.SetCurrentContext` + `ImGuiImplSDL3.ProcessEvent`). Events
   with no window (gamepad, quit) go to both / neither as appropriate.
2. For each window: `SetCurrentContext`, `NewFrame`, build UI, `Render`,
   acquire a command buffer + swapchain texture for *that* window, submit.
3. Tick the system **once**, between the two windows' UI builds (see below).

### Single tick driver

The system must be advanced in exactly one place. Neither window advances it
itself. `Program.cs`'s loop does:

```
if (debuggerHost.Visible && debugger != null)
    debugger.RunForDuration(dt);   // honours breakpoints / single-step / Stopped
else
    system.RunForDuration(dt);     // free-run
```

Both windows then just render whatever state the system is in. When the debugger
window is hidden, `system.RunForDuration` free-runs (no `Stopped` gate), so the
emulation window is live from launch. Opening the debugger hands the clock to
`debugger.RunForDuration`; if the debugger is in its default `Stopped` state the
machine pauses the moment the debugger appears, which is the behaviour a
debugger user expects.

### Alternatives considered

- **One window, view toggle.** Backtick swaps the single window between "bare TV
  image + menu" and "today's dockspace". Simplest (one context, no event
  routing), but doesn't match the "two windows" intent — you can't watch the
  game and the disassembly side by side — and audio/input focus semantics get
  muddled. Kept in mind only as a fallback if the two-context wiring proves
  unstable in the SDL3/SDLGPU backend.
- **One context + multi-viewport** (`ViewportsEnable`). Lets ImGui windows be
  dragged out to OS windows, but you can't cleanly say "this window is always
  its own OS window, toggled by a hotkey" — the user drives that by dragging.
  Rejected as a poor fit for the interaction we want.

## File changes

### New: `src/Aemula.UI/EmulationWindow.cs`

Owns one `SDL_Window` + its ImGui context, and:

- `void SetSystem(EmulatedSystem system)` — swaps which system it renders.
  Every system exposes `EmulatedSystem.Television` (concrete on the base
  class — see "Every system has a `Television`" below), so this just grabs
  `system.Television`.
- **Rendering** — a texture-upload of the active-video region of the
  `Television`, drawn with `ImGui.Image` into a full-window, no-decoration,
  no-padding host window (`ImGuiWindowFlags.NoDecoration | NoBringToFrontOnFocus`,
  zero `WindowPadding`, docked to the viewport), aspect-corrected via the same
  `Television.ComputeVerticalStretchFactor` / `CalculateSizeFittingAspectRatio`
  math `TelevisionWindow` already uses. No region overlays, no crosshair, no
  sidebar, no hover tooltip — deliberately just the picture.
  - Factor the shared upload/aspect code out of `TelevisionWindow` into a small
    helper (e.g. `TelevisionTextureView`) that both classes use, rather than
    copy-pasting `CreateGpuResourcesForCurrentSize` / `PrepareOverride`. Keep
    `TelevisionWindow` as the debugger-side wrapper that adds the overlays.
- **Menu bar** — `DrawMenuBar()`; see "File menu" below.
- **Input** — `HandleKeyEvent(SDLKeyboardEvent)` / gamepad forwarding to
  `system.OnKeyEvent`. The emulation window always forwards system input
  regardless of `io.WantCaptureKeyboard` (its only ImGui interactables are
  menus, which capture only while open).
- Placeholder hook for the future audio stream (`SDL_OpenAudioDeviceStream`),
  called out with a comment, not implemented.

### New: `src/Aemula.UI/DebuggerHost.cs`

Everything debugger-related lifted out of `Program.cs`:

- Owns one `SDL_Window` (titled "Aemula — Debugger"), its ImGui context, the
  dockspace builder (`DrawWindow` / first-run `DockBuilder` layout), the
  `List<DebuggerWindow>`, the `Windows` menu, the perf readout
  (`DrawMainMenu`), and the `ImGuiSettingsHandler` ini wiring
  (`ImGuiSettingsReadOpen` / `ReadLine` / `WriteAll`).
- `bool Visible { get; }` + `Show()` / `Hide()` — `SDL_ShowWindow` /
  `SDL_HideWindow`. The window and context are created once, lazily on first
  `Show()`; toggling afterwards just shows/hides.
- `void SetSystem(EmulatedSystem system, Debugger? debugger)` — disposes the old
  `DebuggerWindow`s, rebuilds via `debugger.CreateDebuggerWindows`, re-runs
  `CreateGraphicsResources(gpuDevice)` on each, refreshes the GCHandle list the
  settings handler closes over, and forces a fresh first-run dock layout.
- Keeps its own `io.IniFilename = "imgui.debugger.ini"` so the emulation
  window's trivial layout doesn't fight the debugger's persisted one. (The
  existing `imgui.ini` becomes the debugger's; emulation window can set
  `IniFilename = null`.)

### New: `src/Aemula.UI/SystemCatalog.cs`

Replaces the bare `Program.Systems` dictionary with an ordered list of entries:

```csharp
sealed record SystemCatalogEntry(
    string Id,                 // "appleii", "atari2600", …
    string DisplayName,        // "Apple II+", "Atari 2600", …
    Func<EmulatedSystem> Create,
    RomRequirement Rom,        // None | Optional | Required
    string RomDialogTitle,     // "Select a cartridge", …
    SDLDialogFileFilter[] RomFilters);

enum RomRequirement { None, Optional, Required }
```

Per current `LoadProgram` behaviour:

| System         | Rom            | Notes |
|----------------|----------------|-------|
| `appleii`      | **Optional**   | Bundled ROM boots to BASIC; file overrides `$D000-$FFFF` (`.rom`, `.bin`). |
| `atari2600`    | **Required**   | `LoadProgram` does `File.ReadAllBytes(filePath)` — needs a cartridge (`.a26`, `.bin`). |
| `spaceinvaders`| **None**       | Loads four fixed ROM files from the build output; `filePath` ignored. |
| `nes`          | **Required**   | `Cartridge.FromFile(filePath)` (`.nes`). |

`Atari2600System.LoadProgram` currently throws on a null/empty path — with
`RomRequirement.Required` the emulation window won't call `SetSystem` for it
until a file has been chosen (File ▸ Open picks the file *first*, then swaps).

Default system on launch: **`appleii`** (boots to BASIC with no file needed),
unless `args` override it.

The `Aemula.Console/SystemRegistry.cs` subset stays as-is — it exists for a
different reason (frame-counting tools that need `Television.CurrentRow`).

### Changed: `src/Aemula.UI/Program.cs`

Shrinks to orchestration:

1. `SDL_Init`, create the GPU device, create the **emulation** `SDL_Window`,
   claim it.
2. Parse args: optional `args[0]` system id, optional `args[1]` file — used only
   to pick the initial `SystemCatalogEntry` and pre-load a file.
3. Build `EmulationWindow`; `DebuggerHost` constructed but not shown.
4. `LoadSystem(entry, filePath)` helper (shared by startup and the File menu):
   dispose old system, `entry.Create()`, `system.LoadProgram(filePath ?? "")`,
   `system.CreateDebugger()`, `emulationWindow.SetSystem(system)`,
   `debuggerHost.SetSystem(system, debugger)`.
5. Main loop: one `SDL_PollEvent` drain with per-window event routing; backtick
   toggle; single tick driver (above); render emulation window; render debugger
   window if visible; perf accounting.
6. Teardown: dispose both hosts, both contexts, both ImPlot contexts, release
   both windows from the GPU device, destroy the device, `SDL_Quit`.

### Changed: `src/Aemula/EmulatedSystem.cs`

`RunForDuration` currently returns `void`. Add a cheap executed-cycle count so
the perf readout works on the free-run path (today it relies on
`Debugger.Ticked`):

- Option A: `RunForDuration` returns `int` (clocks actually ticked).
- Option B: `public ulong TotalCycles { get; private set; }` incremented in the
  loop; `Program` diffs it per perf window.

Option B is less intrusive to callers. Either is fine.

## File menu (emulation window)

```
File
  System ▸            (radio list of SystemCatalog entries; check = current)
      Apple II+
      Atari 2600
      NES
      Space Invaders
  Open ROM…           (enabled iff current system's Rom != None; Ctrl+O)
  Reset               (system.Reset(); Ctrl+R)
  ─────
  Quit                (Cmd/Alt+F4 equivalent)
View
  Debugger            (checkbox, backtick) → debuggerHost.Show()/Hide()
```

- **Choosing a system** from the submenu:
  - `Rom == None` or `Optional` → `LoadSystem(entry, null)` immediately.
  - `Rom == Required` → open the file dialog first; only `LoadSystem` on a
    successful pick. Cancel leaves the current system running.
- **Open ROM…** → `SDL.ShowOpenFileDialog` (async, callback-based) with the
  entry's `RomFilters` and `RomDialogTitle`, parented to the emulation window.
  In the callback, `LoadSystem(currentEntry, pickedPath)` (marshal back onto the
  main loop — set a `pendingLoad` field the loop consumes, don't touch GPU/ImGui
  state from the SDL callback thread).
- Later: a **Disk** entry appears here once disk images are supported; the
  catalog entry grows a `DiskRequirement` alongside `RomRequirement`.

## Backtick hotkey

In the event drain, before routing a `KeyDown` to a context: if
`e.key.key == SDLK_GRAVE` and no text input is active, toggle
`debuggerHost` visibility and swallow the event (don't forward to the system or
to ImGui). Backtick is rare enough as an emulated-machine key that a plain
binding is acceptable for v1; note in a comment that it should become
rebindable / modifier-guarded if a supported system needs the key.

## imgui.ini / persistence

- Debugger context: `io.IniFilename` → `"imgui.ini"` (unchanged file, so
  existing layouts survive), keeps the `ImGuiSettingsHandler` for
  `DebuggerWindow.IsOpen` + per-window state.
- Emulation context: `io.IniFilename = null` (nothing worth persisting; the
  layout is one forced full-bleed window).
- Persist last-used system id + last ROM path? Deferred — nice-to-have, not in
  scope. When added it belongs in a small `Aemula.UI` settings file, not
  `imgui.ini`.

## Every system has a `Television`

`Television` is now a concrete property on `EmulatedSystem`, populated for every
system (`appleii`, `atari2600`, `spaceinvaders`, `nes` — `nes` decodes its
composite output through `Television` like the rest). The old `IHasTelevision`
interface and the per-system `DisplayBuffer` fallback render path are gone, so
the emulation window always uses the `Television` upload path and `SetSystem`
needs no branching. `ScreenDisplayWindow` / `DisplayBuffer` survive only as a
debugger-side window some systems still add (Apple II, Space Invaders).

## Input & focus

- Emulation window focused → keys/gamepad go to `system.OnKeyEvent`.
- Debugger window focused → today's `io.WantCaptureKeyboard` gate applies within
  that context; system input only flows from the emulation window.
- Gamepad events (`SDLInitFlags.Gamepad` already requested) have no window id —
  forward to `system.OnKeyEvent` unconditionally (they're only meaningful to the
  machine).

## Deferred (explicitly out of scope here)

- Audio output (only the SDL audio-stream hook point is stubbed).
- Disk image loading / the File ▸ Disk menu.
- Rebindable keys; backtick stays hard-coded.
- Persisting last system / ROM across runs.
- Any change to the debugger windows themselves.
- Removing `ScreenDisplayWindow` (still added as a debugger window by Apple II
  and Space Invaders).

## Implementation steps

1. **Extract the TV upload helper** from `TelevisionWindow` into
   `TelevisionTextureView` (no behaviour change; `TelevisionWindow` still
   renders identically). Run the `*TelevisionTests` / relevant UI-adjacent
   tests.
2. **`SystemCatalog.cs`** — entries + `RomRequirement` + filters. Point
   `Program.Systems` usages at it.
3. **`EmulatedSystem` cycle count** (Option B) for the perf readout.
4. **`DebuggerHost.cs`** — move `DrawWindow`, `DrawMainMenu`, the settings
   handler, the dock-builder first-run, and the `DebuggerWindow` list out of
   `Program.cs` behind `Show/Hide/SetSystem`. At this stage still the only
   window; app behaves as today but through the new class.
5. **Second ImGui context + event routing** — stand up `DebuggerHost`'s window
   as a genuinely separate `SDL_Window` sharing the GPU device; verify two
   contexts render and take input independently.
6. **`EmulationWindow.cs`** — window + context + full-bleed TV render +
   `SetSystem`. Wire `Program.cs` to create it as the primary window; debugger
   starts hidden.
7. **File menu** — System submenu + Reset + Quit + View ▸ Debugger, driven by
   `LoadSystem`.
8. **Open ROM…** — `SDL.ShowOpenFileDialog`, `pendingLoad` marshalling, the
   Required-system "pick before switch" flow.
9. **Backtick toggle** + menu checkbox sync.
10. **Args become optional** — default to `appleii`; `args[0]`/`args[1]` just
    seed the initial `LoadSystem`. Update `launchSettings.json` comment/profile.
11. Teardown paths for both windows/contexts; check clean exit (no GPU
    validation errors on `SDL_WaitForGPUIdle`).

## Risks

- **Two live ImGui + SDLGPU backend instances.** The Hexa.NET backends key their
  state off the current context, so per-context init is expected to work, but
  this is the first place the codebase does it. Step 5 is the go/no-go: if the
  backend can't cleanly host two contexts, fall back to the one-window view
  toggle (design note above) and keep everything else in this plan.
- **File dialog threading.** `SDL.ShowOpenFileDialog`'s callback fires off the
  event loop; all it may do is stash the path — the actual system swap happens
  on the main loop.
- **System swap mid-frame.** `LoadSystem` must run between frames, never from an
  event callback, since it disposes GPU resources the debugger windows hold.
