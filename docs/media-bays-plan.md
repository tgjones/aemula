# Media Bays — Implementation Plan

## Goal

Replace `EmulatedSystem.LoadProgram(string)` with a generic, system-driven model
of **what removable media a machine accepts and where**. A cartridge, a
cassette, a floppy (eventually, more than one drive) are different things going
into different receptacles; one `string` path can't describe any of that, and
the "is a file required / optional / ignored, and what extensions" knowledge is
currently split between each `LoadProgram` body and a hand-maintained table in
`Aemula.UI`'s `SystemCatalog`.

After this change:

- A system (and any peripheral cabled to it) declares a list of **media bays** —
  `MediaBay { Id, DisplayName, Required, Filters, DialogTitle }`. The UI builds
  its "Media" menu and the CLI builds `--media` / `--list-media` straight off
  that list, with no per-system code.
- The machine's **fixed boot ROMs** (Space Invaders' four ROMs, the Apple II
  base image) stop being a "load" at all — they move into the system
  constructor. Space Invaders ends up with no media surface whatsoever.
- The Apple II `$D000–$FFFF` **override image** becomes a construction option
  (`AppleIISystemOptions`), not media — see
  [Apple II high-ROM override](#apple-ii-high-rom-override).
- `Rig` is the single façade: `rig.MediaBays`, `rig.InsertMedia(bayId, image)`,
  `rig.EjectMedia(bayId)`. Callers never learn whether a bay is intrinsic to the
  machine (a cartridge slot) or contributed by a peripheral (the cassette deck).

This is the sibling of the just-landed expansion-slots work and deliberately
mirrors its shape: a plain-data capability list in core, presentation in the UI,
a repeatable `--x <id>=<value>` CLI arg, and a rebuild-the-`Rig` path for the
one thing that can't change hot.

Non-goals are under [Out of scope](#out-of-scope).

## Current state

- **`EmulatedSystem.LoadProgram(string filePath)`** — `abstract`. Five systems,
  five contracts:
  | System | Behaviour |
  |---|---|
  | Space Invaders | ignores the path; loads four fixed ROMs from `AppContext.BaseDirectory` |
  | Apple II | always loads the bundled base ROM; the path *optionally* overlays `$D000–$FFFF`; throws `InvalidDataException` on a bad size; then `Reset()` + `RaiseProgramLoaded()` |
  | Apple I | ignores the path; just `Reset()` + `RaiseProgramLoaded()` — a `.wav` is taken by the `CassetteDeck` peripheral, not here |
  | Atari 2600 | `File.ReadAllBytes` → `Cartridge.FromData` → `RaiseProgramLoaded()` |
  | NES | `Cartridge.FromFile` → insert → `Reset()` |
- **`Rig.LoadProgram(string)`** — offers the path to each peripheral's
  `IPeripheral.TryLoadMedia` first (the cassette deck accepts `.wav`), else calls
  `System.LoadProgram`.
- **`EmulatedSystem.ProgramLoaded`** event — sole subscriber is `Debugger`
  (`Disassembler.Reset()` when the code map changes).
- **`SystemCatalog` / `SystemCatalogEntry` / `RomRequirement` / `RomFileFilter`**
  (`Aemula.UI`) — layers `(RomRequirement, RomDialogTitle, RomFilters)` onto
  `EmulatedSystems.All` keyed by `Id`. `RomRequirement` (`None` / `Optional` /
  `Required`) drives whether "Open ROM" is enabled and whether the file dialog
  opens *before* the system swap. This is the only thing `SystemCatalog` adds
  over `EmulatedSystems`.
- **`Aemula.Console`** — `--rom <path>` (single), passed to `rig.LoadProgram`.
- **`Aemula.Benchmarks`** — `SystemSpec.WorkloadPath : Func<string>`;
  `SystemBenchmark` / `ProfileHarness` / `DebuggerOverheadBenchmark` build the
  system and call `system.LoadProgram(spec.WorkloadPath())`.

Everything except `Aemula.UI` / `Aemula.Console` / `Aemula.Benchmarks` is in the
one `Aemula` assembly.

## Design

### 1. `MediaImage` — bytes with a name

```csharp
// src/Aemula/Emulation/Systems/MediaImage.cs
namespace Aemula.Emulation.Systems;

public sealed class MediaImage
{
    public string Name { get; }                 // original filename — display + extension sniffing
    public ReadOnlySpan<byte> Data { get; }      // backed by a byte[]
    public Stream OpenRead();                    // new MemoryStream(bytes, writable: false)

    public static MediaImage FromFile(string path);              // name = Path.GetFileName(path)
    public static MediaImage FromBytes(string name, byte[] data);
}
```

Systems and peripherals take a `MediaImage` instead of doing their own
`File.ReadAllBytes` / `File.OpenRead`. Side effect: the NES tests drop their
`WriteTempRom` temp-file dance and build images in memory.

### 2. `MediaBay` — generic capability data

```csharp
// src/Aemula/Emulation/Systems/MediaBay.cs
namespace Aemula.Emulation.Systems;

public sealed record MediaBay(
    string Id,                                   // "cartridge", "cassette", "drive1"
    string DisplayName,                          // "Cartridge slot", "Cassette recorder"
    bool Required,                               // advisory — see below
    IReadOnlyList<MediaFileFilter> Filters,
    string DialogTitle);

public readonly record struct MediaFileFilter(string Name, string Pattern);   // moved from Aemula.UI's RomFileFilter
```

No `MediaKind` and no hot/cold flag:

- Nothing switches on a "kind". Filters and dialog title are already fields;
  required-ness is its own field; the byte-handling lives in whoever owns the
  bay. `DisplayName` carries the human word.
- Hardware didn't enforce power-off-to-swap (you *could* yank an Atari
  cartridge mid-frame), so neither do we. `InsertMedia` / `EjectMedia` are
  valid any time; pulling a cartridge under a running CPU misbehaves exactly
  like the real thing. The clean way to change one is a [Hard
  Reset](#5-soft-and-hard-reset).
- **`Required` is advisory.** A cartridge console with no cartridge doesn't
  throw — it runs off into open bus. The UI uses `Required` to open the file
  dialog proactively instead of showing a black screen; the CLI uses it to emit
  a friendly error instead of a garbage screenshot.

The hardware-specific term lives only in `DisplayName` — "Cartridge slot",
"Cassette recorder", "Disk drive 1" — exactly as the expansion work put "Slot 3"
in `ExpansionSlot.DisplayName` while the type stayed generic.

### 3. `IMediaBayHost` — the seam

```csharp
// src/Aemula/Emulation/Systems/IMediaBayHost.cs
public interface IMediaBayHost
{
    IReadOnlyList<MediaBay> MediaBays => [];
    void InsertMedia(string bayId, MediaImage image) => throw new ArgumentException($"No media bay '{bayId}'.");
    void EjectMedia(string bayId) => throw new ArgumentException($"No media bay '{bayId}'.");
}
```

Implemented three times:

- **`EmulatedSystem : IMediaBayHost`** — default (empty) for the ROM-fixed
  systems; overridden by Atari 2600 and NES for their `cartridge` bay.
  `abstract LoadProgram` is **removed**; `ProgramLoaded` is renamed
  **`MediaChanged`** (`EventArgs` unchanged) and raised from
  `InsertMedia` / `EjectMedia`.
- **`IPeripheral : IMediaBayHost`** — `TryLoadMedia` is **removed**;
  `CassetteDeck` overrides for its `cassette` bay.
- **`Rig : IMediaBayHost`** — `MediaBays` is the system's own concatenated with
  every peripheral's (a bay-id collision throws in the `Rig` constructor).
  `InsertMedia` / `EjectMedia` find the owning host by bay id and forward.
  `Rig.LoadProgram` is **removed**. `Rig` re-surfaces `System.MediaChanged`.

### 4. Reset semantics

No `Reset()` anywhere in the media path. The two cartridge systems currently
differ — the NES `LoadProgram` calls `Reset()`, the Atari's doesn't — because a
cartridge console is *inert without a cartridge*: nothing to fetch a reset
vector from. That is modelled as:

> `InsertMedia` into a `cartridge` bay pulses the CPU reset line **only when the
> machine hasn't started yet** (`TotalCycles == 0`) — completing power-on.
> Inserting later just changes the bytes under the running CPU.

Uniform for both (harmless re-pulse of an idle 6507 on the Atari, necessary on
the NES), needs no per-test `Reset()`, and reuses the `TotalCycles` counter
already on `EmulatedSystem`. A clean cartridge change is a [Hard
Reset](#5-soft-and-hard-reset), which reconstructs the system at `TotalCycles ==
0` so the fresh `InsertMedia` pulses reset again.

This also retires the "create the debugger *before* `LoadProgram`" ordering note
in the NES/Atari debugger tests: the debugger subscribes to `MediaChanged` and
resets its disassembler whenever it fires, regardless of order.

### 5. Soft and Hard Reset

Two operations, matching the near-universal emulator convention:

- **Soft Reset** (`rig.Reset()`, unchanged) — pulse the RES line. RAM and
  registers survive; the CPU re-vectors. The physical RESET button/key.
- **Hard Reset** (new) — reconstruct the `EmulatedSystem` from cold: RAM back
  to its power-on state, chips re-built, counters re-seeded, `TotalCycles → 0`,
  **same** slot configuration and **same** inserted media. The only clean way
  to change a cartridge, and the only way out of a wedged machine.

Hard Reset is **not a new core method**. It is the rebuild path the UI already
runs for a system swap and a card change — dispose the `Rig`,
`descriptor.Build(slotConfig)`, re-apply the retained media, re-wire the
debugger — invoked with the *current* descriptor. `v1a`: the whole `Rig` is
rebuilt, peripherals included, so a loaded tape reverts to freshly-inserted (a
real deck would keep its state, but that is explicitly
[out of scope](#out-of-scope)). `Aemula.Console` needs nothing here — every run
already starts cold; tests that want a cold start just construct a fresh system.

### 6. No `MediaConfiguration` type

Slots got `ExpansionSlotConfiguration` because it is resolved against a catalog
with defaulting and validation. Media has none of that. The UI and CLI hold a
plain `Dictionary<string, MediaImage>` (mirroring how `Aemula.UI`'s `Program`
already holds `slotChoices`) and re-apply it with `rig.InsertMedia(...)` after
any rebuild. `descriptor.Build(slotConfig)` is unchanged — no media passed in;
the caller inserts afterward.

### 7. Apple II high-ROM override

Becomes a construction option, per the "revisit motherboard-level components
later" appetite from the expansion plan.

```csharp
// src/Aemula/Emulation/Systems/AppleII/AppleIISystemOptions.cs
public readonly struct AppleIISystemOptions
{
    public static readonly AppleIISystemOptions Default = new();

    // A full 12K set or a shorter diagnostic/monitor image mapped into the top
    // of $D000–$FFFF, the lower sockets left showing the bundled Applesoft. An
    // image longer than 12K throws InvalidDataException from the constructor.
    public readonly byte[]? HighRomOverride;

    public AppleIISystemOptions(byte[]? highRomOverride = null) => HighRomOverride = highRomOverride;
}
```

`EmulatedSystems.All`'s `appleii` entry constructs `new AppleIISystem()`
(unchanged — `Default`). The four override-path tests construct with the option;
the size check moves from `LoadProgram` to the constructor.

**Accepted regression:** the UI's "Open ROM" for Apple II and
`aemula-console --rom appleii …` stop doing anything until a later
motherboard-options pass. `appleii` is `Optional` today (boots to BASIC unaided),
so nothing that must boot stops booting.

## New / changed types

| Type | File | Notes |
|---|---|---|
| `MediaImage` | `Emulation/Systems/MediaImage.cs` | new |
| `MediaBay`, `MediaFileFilter` | `Emulation/Systems/MediaBay.cs` | new; `MediaFileFilter` supersedes `Aemula.UI`'s `RomFileFilter` |
| `IMediaBayHost` | `Emulation/Systems/IMediaBayHost.cs` | new seam, default members |
| `AppleIISystemOptions` | `Emulation/Systems/AppleII/AppleIISystemOptions.cs` | new |
| `EmulatedSystem` | `EmulatedSystem.cs` | remove `abstract LoadProgram`; `ProgramLoaded` → `MediaChanged`; implement `IMediaBayHost` (empty defaults); `RaiseProgramLoaded` → `RaiseMediaChanged` |
| `IPeripheral` | `Emulation/Peripherals/IPeripheral.cs` | remove `TryLoadMedia`; implement `IMediaBayHost` (empty defaults) |
| `Rig` | `Rig.cs` | remove `LoadProgram`; implement `IMediaBayHost` (aggregate + dispatch, collision check); re-surface `MediaChanged` |
| `Atari2600System` | `.../Atari2600/Atari2600System.cs` | remove `LoadProgram`; `cartridge` `MediaBay`; `InsertMedia`/`EjectMedia`; reset-if-`TotalCycles==0` |
| `NesSystem` | `.../Nes/NesSystem.cs` | as Atari; drop the internal `Reset()` |
| `Nes/Cartridge` | `.../Nes/Cartridge.cs` | `FromFile(string)` → `FromImage(MediaImage)` (read via `image.OpenRead()`) |
| `Atari2600/Cartridge` | `.../Atari2600/Cartridge.cs` | `FromData(byte[])` accepts `ReadOnlySpan<byte>` (or `MediaImage`) |
| `SpaceInvadersSystem` | `.../SpaceInvaders/SpaceInvadersSystem.cs` | remove `LoadProgram`; four-ROM load → constructor |
| `AppleISystem` | `.../AppleI/AppleISystem.cs` | remove `LoadProgram` (constructor already resets/seeds) |
| `AppleIISystem` | `.../AppleII/AppleIISystem.cs` | remove `LoadProgram`; base-ROM load + `HighRomOverride` + `Reset()` → constructor; `AppleIISystem(AppleIISystemOptions)` |
| `CassetteDeck` | `Emulation/Peripherals/Cassette/CassetteDeck.cs` | remove `TryLoadMedia`; `cassette` `MediaBay`; `InsertMedia`/`EjectMedia` → `InsertTape`/`EjectTape` (WAV via `WavReader.Read(image.OpenRead())`) |
| `Debugger` | `Debugging/Debugger.cs` | subscribe to `System.MediaChanged` instead of `ProgramLoaded` |
| `SystemCatalog`, `SystemCatalogEntry`, `RomRequirement`, `RomFileFilter` | `Aemula.UI/SystemCatalog.cs` | **deleted** — the UI uses `EmulatedSystems` directly |
| `EmulationWindow.Callbacks` | `Aemula.UI/EmulationWindow.cs` | typed on `SystemDescriptor`; Media submenu; `HardReset` callback |
| `Aemula.UI/Program.cs` | — | retained media dict; `HardReset` pending action; media dialog keyed by `MediaBay`; try/catch around `InsertMedia` |
| `Aemula.Console/Program.cs` | — | remove `--rom`; add `--media <bay>=<path>` (repeatable) and `--list-media <system>` |
| `SystemSpec` | `Aemula.Benchmarks/Benchmarks/SystemSpecs.cs` | `WorkloadPath : Func<string>` → `Media : Func<IReadOnlyList<(string BayId, MediaImage Image)>>` |

## CLI surface (generic)

Mirrors `--slot` / `--list-slots`:

- `--media <bayId>=<path>` — repeatable. `nes` / `atari2600` take
  `--media cartridge=<path>`.
- `--list-media <system>` — builds a `Rig` with the parsed `--slot` config (so
  `--list-media applei --slot expansion=none` correctly shows no cassette bay)
  and prints each bay: `Id`, `DisplayName`, `Required`, and its filters. No
  system is run.
- A `Required` bay with nothing given → friendly error out of the existing
  top-level catch, not a run.
- `Program.Run` builds the `Rig`, then `foreach ((bayId, path) in mediaChoices)
  rig.InsertMedia(bayId, MediaImage.FromFile(path))` before frame 0.
- `--rom` is removed outright — no alias. Existing callers move to
  `--media cartridge=<path>`.

## UI surface (generic)

- `SystemCatalog` is gone; the `File > System` submenu is built from
  `EmulatedSystems.All`, `Default` = `All[0]`.
- A **Media** submenu (under `File`), shown when `rig.MediaBays` is non-empty.
  One entry per bay: **Insert…** (opens the file dialog with that bay's
  `Filters` / `DialogTitle`) and **Eject** (enabled when the retained dict has
  an image for that bay).
- Choosing a system builds the new `Rig` immediately; if it has a `Required`
  bay and the (cleared) media dict has nothing for it, the file dialog opens
  and the pick is `InsertMedia`'d. Cancel disposes the fresh `Rig` and keeps
  the running one (a few ms of wasted construction on cancel — acceptable).
- **Soft Reset** and **Hard Reset** menu items (see [Soft and Hard
  Reset](#5-soft-and-hard-reset)): **Soft Reset** calls `rig.Reset()`; **Hard
  Reset** is a pending action drained between frames that rebuilds via the
  current descriptor and re-applies the media dict.
- `InsertMedia` is now user-triggered at arbitrary times, so `Program` wraps it
  in try/catch — a truncated/!valid file shows a message, not a crash.
- The SDL filter-marshalling in `ShowOpenRomDialog` is unchanged in mechanism,
  just parameterised by a chosen `MediaBay` rather than a `SystemCatalogEntry`.

## Benchmarks

`SystemSpec.Media` returns `[]` for `appleii` / `applei` / `spaceinvaders` and a
single `("cartridge", <image>)` for `atari2600` / `nes` (`Workloads.*` return a
`MediaImage`). `SystemBenchmark.Setup` / `ProfileHarness.Run` /
`DebuggerOverheadBenchmark` build the system and
`foreach ((bayId, image) in spec.Media()) system.InsertMedia(bayId, image)`
before warmup. Mechanical.

## Phases

Each phase leaves the tree green.

1. **Core seam, no behaviour change.** Add `MediaImage`, `MediaBay`,
   `MediaFileFilter`, `IMediaBayHost`. `EmulatedSystem` implements
   `IMediaBayHost` (empty), `ProgramLoaded` → `MediaChanged`, `Debugger`
   follows. `IPeripheral` implements `IMediaBayHost` (empty), keeps
   `TryLoadMedia`. `Rig` implements `IMediaBayHost` (aggregate + dispatch) and
   keeps `LoadProgram` unchanged. Nothing calls the new path.
2. **Cartridge systems.** Atari 2600 + NES: `cartridge` `MediaBay`,
   `InsertMedia` / `EjectMedia` (reset-if-`TotalCycles==0`), remove their
   `LoadProgram`. `Cartridge.FromImage` / `FromData(ReadOnlySpan<byte>)`.
   Repoint the Atari/NES tests to `InsertMedia`.
3. **ROM-fixed systems + kill `LoadProgram`.** Space Invaders four-ROM load →
   constructor. Apple I: delete `LoadProgram`. Apple II: base ROM + override +
   `Reset()` → constructor; add `AppleIISystemOptions.HighRomOverride`. Remove
   `EmulatedSystem.LoadProgram` and `Rig.LoadProgram`. Update every
   `LoadProgram("")` call site (delete the line) and the Apple II override
   tests (construct with the option; expect the ctor to throw).
4. **Cassette peripheral.** `CassetteDeck`: `cassette` `MediaBay`,
   `InsertMedia` / `EjectMedia`. Remove `IPeripheral.TryLoadMedia` and the
   deck's implementation.
5. **CLI.** `--media`, `--list-media`, remove `--rom`, validation. Arg-parse
   and validation tests.
6. **UI.** Delete `SystemCatalog` / `RomRequirement`; Media submenu; Soft/Hard
   Reset menu items; retained media dict; try/catch; dialog keyed by `MediaBay`.
7. **Benchmarks.** `SystemSpec.Media`; harness loops.

## Testing

- **`MediaImage`** — `FromFile` sets `Name` to the filename; `OpenRead` yields
  the bytes.
- **`Rig` media aggregation** — a slotless cartridge system exposes exactly its
  own bay; `applei` with the ACI exposes the deck's `cassette` bay and nothing
  else; `applei --slot expansion=none` exposes no bays; a duplicate bay id
  across system + peripheral throws in the `Rig` constructor.
- **Routing** — `rig.InsertMedia("cartridge", …)` reaches the system;
  `rig.InsertMedia("cassette", …)` reaches the deck; an unknown bay id throws;
  `EjectMedia` clears.
- **Reset rule** — Atari/NES `InsertMedia("cartridge", …)` before the first
  `Tick` leaves the CPU able to boot (reads the vector from the cartridge);
  inserting after ticking has begun does **not** pulse reset.
- **Regression** — `AppleICassetteTests` (real archive.org `BASIC.wav` end to
  end) stays green with its `LoadProgram("")` line removed; the tape still goes
  in via the deck. `Atari2600` / `NES` television and audio tests green after
  the `InsertMedia` repoint.
- **Apple II option** — a short image overlays the F8 socket and the lower
  sockets still show the bundled ROM; a full 12K image replaces the space; a
  `> 12K` image throws `InvalidDataException` from the constructor;
  `apple2dead.bin` still reaches "ZERO/STACK PAGES OK".
- **CLI** — `--list-media atari2600` output; `--media cartridge=<path>` boots;
  `atari2600` with no `--media` exits non-zero with a message; a bad
  `--media` value exits non-zero.
- **Hard Reset (UI)** — covered by a `Program`-level rebuild test if one is
  practical, else by the existing rebuild-path coverage plus a manual check.

## Out of scope

- **`GetInsertedMedia` on `IMediaBayHost`.** The UI/CLI track their own media
  dict; add a query later if something needs it.
- **Faithful power-cycle** that keeps peripheral instances and their state (tape
  still threaded at position) — needs `PeripheralRequest` to split `Create`
  from `Attach`. Explicitly rejected: not worth it.
- **A persistent power on/off toggle.** Only the one-shot Hard Reset now.
- **Multiple identical bays from one dual-drive peripheral.** Model a
  twin-drive unit as two peripheral instances, each with one bay.
- **Generic motherboard / DIP-switch options** (a system-agnostic way to expose
  `AppleIISystemOptions` / `AppleISystemOptions` / PAL-NTSC in the CLI and UI).
  Same deferral as the expansion-slots plan.
- **Drag-and-drop a file onto the window** auto-routing to a matching bay by
  extension. Plausible follow-up; not here.
- **Save / write-back media.** `CassetteDeck.SaveRecording` stays its own API.

## Decisions settled

- **Name** — `MediaBay`. Generic `Media`-prefixed types
  (`MediaImage`, `MediaBay`, `MediaFileFilter`, `IMediaBayHost`), the hardware
  word in `DisplayName` only.
- **No `MediaKind`, no hot/cold flag, no insert-time enforcement.** Yanking a
  cartridge mid-frame misbehaves like the hardware.
- **`Required` is advisory** — a UI/CLI affordance, not an invariant.
- **Fixed boot ROMs move to constructors**; the Apple II high-ROM override
  becomes `AppleIISystemOptions` (accepted UI/CLI regression until a
  motherboard-options pass).
- **No `MediaConfiguration` type** — callers hold a `Dictionary<string,
  MediaImage>` and re-apply after a rebuild; `descriptor.Build` is unchanged.
- **Soft Reset / Hard Reset** — Soft Reset is `rig.Reset()` (RES line); Hard
  Reset is the existing `Rig`-rebuild path (v1a: whole `Rig`, peripherals
  included), a UI command, no new core API.
- **`--rom` is removed**, no alias — the CLI takes `--media <bayId>=<path>`.
- **Cartridge insert pulses reset only when `TotalCycles == 0`.**
- **`ProgramLoaded` → `MediaChanged`**, raised by `InsertMedia` / `EjectMedia`.
