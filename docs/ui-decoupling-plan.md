# Decouple Aemula.UI from Aemula — Implementation Plan

## Goal

`Aemula.csproj` should stop referencing `Hexa.NET.ImGui`, `Hexa.NET.ImGui.Backends.SDL3`,
`Hexa.NET.ImPlot`, and `Hexa.NET.SDL3` entirely. Every ImGui-rendered window
moves into `Aemula.UI.csproj`. Debugger *support* — breakpoints, disassembly,
the logic-analyzer recording model, per-system `Debugger` subclasses — stays in
`Aemula`; only the ImGui rendering of it moves.

The one non-mechanical piece: `EmulatedSystem.OnKeyEvent` and five system
overrides are typed directly against `Hexa.NET.SDL3.SDLKeyboardEvent` today.
That's real core-emulation input plumbing (keyboard/joystick handling), not a
UI concern, so it needs a neutral replacement owned by `Aemula` rather than a
straight file move.

## Current state

**Packages.** `Aemula.csproj` declares all four Hexa.NET packages.
`Aemula.UI.csproj` references none of them directly — it gets them
transitively through its `ProjectReference` to `Aemula.csproj`.

**Pure UI files (mechanical move, no logic changes).** Everything under
`src/Aemula/UI/` except the `LogicAnalyzer/` data model (see below), plus five
per-chip `UI/` subfolders that contain nothing but window classes:

- `Aemula/UI/`: `DebuggerWindow.cs`, `DisassemblyWindow.cs`,
  `BreakpointsWindow.cs`, `ScreenDisplayWindow.cs`, `TelevisionWindow.cs`,
  `TelevisionTextureView.cs`, `MemoryEditor.cs`, `ImGuiUtility.cs`,
  `Pane.cs` (enum, used only by the window classes above)
- `Emulation/Chips/Intel8080/UI/CpuStateWindow.cs`
- `Emulation/Chips/Mos6502/UI/CpuStateWindow.cs`
- `Emulation/Chips/Ricoh2C02/UI/{PaletteWindow,PpuStateWindow}.cs`
- `Emulation/Chips/Tia/UI/TiaWindow.cs`
- `Emulation/Systems/Nes/UI/PatternTableWindow.cs`

**`LogicAnalyzer/` must be split, not moved whole.** `Channel.cs`,
`ChannelGroup.cs`, `ChannelKind.cs`, `ChannelNode.cs`,
`LogicAnalyzerRecorder.cs`, `SampleClock.cs` have no ImGui/SDL dependency —
they're the recording model that `Mos6502Chip`, `TiaChip`, `Mos6532Chip`,
`Intel8080Chip`, and several system debuggers wire channels into directly.
Only `LogicAnalyzerWindow.cs` (the ImGui rendering of that data) is a UI
concern and moves.

**The `SDLKeyboardEvent` entanglement.** `EmulatedSystem.OnKeyEvent(SDLKeyboardEvent)`
(`Aemula/EmulatedSystem.cs:90`) is overridden by five systems, all in core
system-logic files, not `UI/` folders:

| File | Uses from `SDLKeyboardEvent` |
|---|---|
| `Emulation/Systems/AppleI/AppleISystem.Keyboard.cs` | `Type`, `Scancode`, `Mod` (Gui, Shift), `Key` (fallback) |
| `Emulation/Systems/AppleII/AppleIISystem.Keyboard.cs` | `Type`, `Scancode`, `Mod` (Gui, Shift+Alt+Mode, Ctrl), `Key` (fallback + held-key identity) |
| `Emulation/Systems/Atari2600/Atari2600System.Input.cs` | `Type`, `Key` (direct match) |
| `Emulation/Systems/Nes/NesSystem.Input.cs` | `Type`, `Key` (direct match) |
| `Emulation/Systems/SpaceInvaders/SpaceInvadersSystem.cs` (`OnKeyEvent`) | `Type`, `Key` (direct match) |

It's also driven from two places, not one:

- `Aemula.UI/EmulationWindow.cs:140` — real SDL events.
- `Aemula.Console/InputScript.cs` — the headless `--input` test driver
  *constructs* `SDLKeyboardEvent` values by hand (`Apply`, `TypeCharacter`) to
  replay scripted key presses. `Aemula.Console.csproj` currently gets
  `Hexa.NET.SDL3` transitively through its reference to `Aemula.csproj`, same
  as `Aemula.UI`.
- `Aemula.Tests` also constructs `SDLKeyboardEvent` directly in
  `AppleISystemInputTests.cs` and `Atari2600SystemInputTests.cs` (also via the
  transitive package flow — no explicit SDL reference in `Aemula.Tests.csproj`
  today). `AppleIISystemKeyboardTests.cs` doesn't construct one — it tests
  `AppleIISystem.MapCharToMatrixPosition` directly.

So `Hexa.NET.SDL3` currently reaches four projects (`Aemula`, `Aemula.UI`,
`Aemula.Console`, `Aemula.Tests`) through one transitive chain, and the fix
has to touch all of them, not just move files around `Aemula.UI`.

## Design

### 1. A neutral `Key` enum and `KeyEvent` struct, owned by `Aemula`

The two jobs currently folded into `SDLKeyboardEvent.Key`/`.Scancode`/`.Mod`
split cleanly:

- **A stable, layout-resolved but modifier-independent key identity.**
  Atari2600/NES/SpaceInvaders match this directly against fixed constants
  today (`SdlkUp`, `SdlkX`, literal `0x40000050`, etc). Apple II also uses the
  *identity* form (not the shift-resolved character) to track which physical
  key is currently held, specifically so a key-up releases the right matrix
  crosspoint even if Shift was released first (see the comment above
  `_heldKey` in `AppleIISystem.Keyboard.cs`).
- **A fully resolved character** (Shift/AltGr applied via
  `SDL.GetKeyFromScancode`), needed by Apple I always and Apple II only at
  key-down, to decide which character was actually typed. This resolution is
  inherently host-keyboard-layout-dependent and can't move into `Aemula` — it
  stays in `Aemula.UI`, which still has SDL.

```csharp
// src/Aemula/KeyEvent.cs
namespace Aemula;

public readonly struct KeyEvent
{
    public required bool IsDown { get; init; }
    public required Key Key { get; init; }   // modifier-independent identity
    public char? Character { get; init; }     // Shift/AltGr-resolved; null for non-text keys
    public bool Ctrl { get; init; }
}
```

```csharp
// src/Aemula/Key.cs
namespace Aemula;

public enum Key
{
    None = 0,

    // Printable keys carry their own ASCII value, so the existing
    // char-range switch expressions in AppleISystem/AppleIISystem keep
    // working almost unchanged, just retyped from int/char to Key.
    Space = ' ',
    Return = 0x0D,
    Escape = 0x1B,
    Backspace = 0x08,
    Delete = 0x7F,
    Digit0 = '0', // .. Digit9
    A = 'a',      // .. Z — plus whatever punctuation Apple II's matrix needs

    // Non-printable keys get values outside the ASCII range — deferred to
    // implementation time; only Up/Down/Left/Right/RightShift are known to
    // be needed today (see the usage table above).
    Up = 256,
    Down,
    Left,
    Right,
    RightShift,
}
```

Exact member list (especially how far Apple II's punctuation coverage needs
to go) is deferred to implementation — see [Open risks](#open-risks).

### 2. `EmulationWindow` becomes the SDL→`KeyEvent` boundary

`Aemula.UI/EmulationWindow.cs` is the only place that still touches
`SDLKeyboardEvent`. It:

1. Resolves the character via `SDL.GetKeyFromScancode` exactly as
   `AppleISystem`/`AppleIISystem` do today (that logic moves *out* of the two
   system files and *into* here — one resolution path instead of two
   near-duplicates).
2. Maps `SDLKeyboardEvent.Key`/`.Scancode` to the new `Key` enum via a small
   translation table.
3. Builds a `KeyEvent` and calls `_rig.System.OnKeyEvent(keyEvent)`.

**Open decision:** both Apple I and Apple II currently do an identical "ignore
Cmd/Win chords" check (`mod & SDLKeymod.Gui`). That could be hoisted into step
3 as a shared policy (`EmulationWindow` simply never calls `OnKeyEvent` while
Gui is held) instead of carried through `KeyEvent` as a field — worth deciding
before implementation, not defaulting into it silently.

### 3. `Aemula.Console/InputScript.cs` constructs `KeyEvent` directly

`Apply` (control tokens) sets `Key` from `InputKeyBindings` (unchanged shape,
just a different type) with `Character = null`. `TypeCharacter` (typed text)
sets `Character` and `Key` from the same resolved character. No SDL reference
needed in `Aemula.Console` afterward.

### 4. Package reference changes

- Move `Hexa.NET.ImGui`, `Hexa.NET.ImGui.Backends.SDL3`, `Hexa.NET.ImPlot`,
  `Hexa.NET.SDL3` from `Aemula.csproj` to `Aemula.UI.csproj` (explicit, since
  they no longer arrive transitively).
- `Aemula.Console.csproj` and `Aemula.Tests.csproj` need no SDL package after
  step 3 / test updates — confirm this holds once the migration lands rather
  than assuming it.

## Phased plan

**Phase 0 — `Key` enum + `KeyEvent` struct**
Add `Aemula/Key.cs` and `Aemula/KeyEvent.cs`. No consumers yet. Nail down the
full `Key` member list against every literal currently matched in the five
`OnKeyEvent` overrides plus `AppleIISystem.MapCharToMatrixPosition`'s
punctuation cases.

**Phase 1 — Migrate `EmulatedSystem` and the five system overrides**
`EmulatedSystem.OnKeyEvent` and `InputKeyBindings` switch from
`SDLKeyboardEvent`/`int` to `KeyEvent`/`Key`. Update
`AppleISystem.Keyboard.cs`, `AppleIISystem.Keyboard.cs`,
`Atari2600System.Input.cs`, `NesSystem.Input.cs`,
`SpaceInvadersSystem.cs`'s `OnKeyEvent` accordingly — the Shift/AltGr
resolution logic (the `keyEvent.Scancode != Unknown ? GetKeyFromScancode(...) : keyEvent.Key`
branches) is deleted from both Apple files, since `Character` arrives
pre-resolved.
**Done when:** no file under `Emulation/Systems/` or `EmulatedSystem.cs`
references `Hexa.NET.SDL3`.

**Phase 2 — `EmulationWindow` resolves and translates**
Build the SDL-keycode→`Key` translation table and the Gui-chord decision from
[Design §2](#2-emulationwindow-becomes-the-sdlkeyevent-boundary) into
`EmulationWindow.cs`. `InputScript.cs` switches to constructing `KeyEvent`
directly and drops its `Hexa.NET.SDL3` `using`.
**Done when:** `Aemula.Console.csproj` needs no SDL package reference.

**Phase 3 — Update tests**
`AppleISystemInputTests.cs` and `Atari2600SystemInputTests.cs` construct
`KeyEvent` instead of `SDLKeyboardEvent`. `AppleIISystemKeyboardTests.cs`
likely needs no change (it exercises `MapCharToMatrixPosition` directly), but
confirm that method's signature still makes sense once its two SDLK-arrow
special cases move to switching on `Key` instead of a raw int.
Run targeted tests via `--treenode-filter` for
`AppleISystemInputTests`, `AppleIISystemKeyboardTests`,
`Atari2600SystemInputTests`, plus `TiaInputPortTests` (touches the same input
path on the Atari side) — not the full suite.

**Phase 4 — Move package references**
Move the four `Hexa.NET.*` `PackageReference`s from `Aemula.csproj` to
`Aemula.UI.csproj`. Build `Aemula.UI` and `Aemula.Console` to confirm nothing
else was relying on the transitive flow.
**Done when:** `Aemula.csproj` has zero `Hexa.NET.*` references and the
solution still builds.

**Phase 5 — Move the window files**
Move every file listed under "Pure UI files" in [Current state](#current-state)
into `Aemula.UI`, mirroring the source folder shape but dropping the
now-redundant trailing `UI/` segment, since everything under `Aemula.UI` is
already a UI concern (e.g. `Emulation/Chips/Mos6502/UI/CpuStateWindow.cs`
becomes `Aemula.UI/Chips/Mos6502/CpuStateWindow.cs`, namespace
`Aemula.UI.Chips.Mos6502`). Split `UI/LogicAnalyzer/`: `LogicAnalyzerWindow.cs`
moves to `Aemula.UI/LogicAnalyzer/LogicAnalyzerWindow.cs`;
`Channel.cs`, `ChannelGroup.cs`, `ChannelKind.cs`, `ChannelNode.cs`,
`LogicAnalyzerRecorder.cs`, `SampleClock.cs` stay in `Aemula`.
**Done when:** `Aemula.csproj` has zero `Hexa.NET.*`/ImGui/SDL references of
any kind (package or transitive), and `Aemula.UI` builds and renders
unchanged. (Per standing project convention, this is a build/compile check —
not a manual UI smoke test; the user verifies UI changes themselves.)

## Open risks

- **Exact `Key` enum member list**, especially how much of Apple II's
  matrix-mapping punctuation set (`MapCharToMatrixPosition`'s `'!' '"' '#' ...`
  cases) needs a `Key` member versus living only in `Character`. Apple II
  only needs `Key` for the held-down/held-up identity check, so most
  punctuation may never need to appear in the enum at all — worth confirming
  against the actual matrix-mapping switch during phase 0 rather than
  guessing the full list up front.
- **The Gui-chord filter's home** (per-system, as today, vs. hoisted into
  `EmulationWindow` as shared policy) — flagged as an open decision in
  [Design §2](#2-emulationwindow-becomes-the-sdlkeyevent-boundary); pick one
  before phase 2.
- **`InputScript.TypeCharacter` never sends a key-up**, only a key-down. Fine
  for Apple I (no held-key state), but if Apple II ever gains `--input`
  quoted-text support, its `_heldKey` would be left stuck. Pre-existing
  behavior, not introduced by this refactor — just don't let it slip in as a
  new assumption while touching this code.
