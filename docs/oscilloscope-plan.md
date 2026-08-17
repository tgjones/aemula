# Oscilloscope Window — Implementation Plan

## Goal

Add a dockable `Aemula.UI` debugger window — modeled on a logic
analyzer/oscilloscope (Saleae-style) — that plots emulated signals over
time: CPU pins, bus values, and other "interesting" points per system.
Start digital/bus-only and simple; grow channel coverage, rendering
fidelity, and controls incrementally.

## Status

**Phases 0 through 4 are done** (see git log for `Aemula.UI` — "Implement
oscilloscope phase 0", "Implement oscilloscope phase 1", a follow-up
"label rows to the left" rework driven by review feedback, "Implement
oscilloscope phase 2", "Implement phase 3 of oscilloscope plan", and
"Implement phase 4"), except for measurement cursors, which were
deliberately left out of Phase 4 — see the Phase 4 writeup below for why,
and Phase 5/6 are still ahead. Everything below is written in the present
tense for what's actually built. If you're picking this up in a new
session, the **Rendering** and **Window layout** sections below describe
the current `OscilloscopeWindow.cs` accurately — read those before
changing anything, since the layout went through two false starts before
landing (see "Layout false starts" under Rendering).

## Design decisions

These were confirmed up front since they shape the core data model:

- **Capture model: always-on rolling buffer, not arm/stop.** There's no
  separate "start capture" button. Recording happens for every tick the
  emulator actually executes, which — since all ticking already funnels
  through one place (`Debugger.RunForDuration` → `TickSystem()` →
  `System.Tick()`, confirmed as the only call site in the codebase today)
  — means the capture **freezes for free whenever the debugger is
  paused/stopped**, no extra plumbing needed. When running, the view
  auto-scrolls with "now" at the right edge; when stopped, it holds still
  and you pan/zoom through what's already in the buffer.
- **Bus channels read the chip's existing combined value directly.**
  E.g. the address bus channel samples `Cpu.Address` (already a `ushort`
  property) once per tick, rather than modeling 16 individual bit-signals
  and compositing them. This matches how every chip in the codebase
  already exposes its pins (`Mos6502Chip.Address`, `.Data`, `.RW`, ...) and
  keeps the channel model to two kinds instead of three.
- **Analog channels are deferred entirely.** `Television`/`Oscillator` in
  `Emulation/Output/Television.cs` are still `NotImplementedException`
  stubs — there's no real analog signal (e.g. composite video) to sample
  yet. The channel model starts digital + bus only; a float-sampled analog
  kind gets added when composite video output becomes real work, not
  before.

## Architecture

New code lives in `src/Aemula/UI/Oscilloscope/`, following the existing
`Aemula/UI/*Window.cs` pattern (`DebuggerWindow` subclass, registered per
system from that system's `Debugger.CreateDebuggerWindows`, same as
`CpuStateWindow`/`ScreenDisplayWindow` today).

### Rendering: Hexa.NET.ImPlot

Waveform rendering uses [Hexa.NET.ImPlot](https://www.nuget.org/packages/Hexa.NET.ImPlot/),
pinned to **2.2.9** to match the existing `Hexa.NET.ImGui` version, added
to `Directory.Packages.props` and referenced from `Aemula.csproj` next to
the other `Hexa.NET.*` packages, plus context setup/teardown in
`Aemula.UI/Program.cs` (`ImPlot.CreateContext()` /
`ImPlot.SetImGuiContext(ctx)` alongside the existing ImGui context,
`ImPlot.DestroyContext()` at shutdown).

Digital channels render via **`ImPlot.PlotStairs`** (a plain step-line),
not `PlotDigital` — see "Layout false starts" below for why. Each
channel's y-axis uses **`ImPlot.SetupAxisTicks(ImAxis.Y1, [0,1], 2, ["L","H"])`**
so it reads "L"/"H" instead of raw 0/1. The x-axis is in absolute
sample-index units (one tick = one `Debugger.Ticked` call) and, since Phase
3, is only forced every frame via `SetupAxisLimits(..., ImPlotCond.Always)`
while the debugger is running; once stopped it hands off to ImPlot's own
pan/zoom via `SetupAxisLinks` — see the Phase 3 writeup below for the full
mechanism. Rows themselves carry `ImPlotAxisFlags.NoTickLabels` on X and
draw no time text of their own; the ruler above the table
(`DrawTimescaleRow`) is the single place tick labels render.

Each row also implements its own hover tooltip: `ImPlotFlags.NoMouseText`
turns off ImPlot's built-in reticle (not useful — it just showed raw plot
coordinates), and instead, after the `PlotStairs` call and still inside
`BeginPlot`/`EndPlot`, `ImPlot.IsPlotHovered()` + `ImPlot.GetPlotMousePos()`
find the nearest sample index and `ImGui.SetTooltip($"{channel.Name}: {H or L}")`
shows its value. `Bus` rows reuse the same hover pattern (see below),
showing the hex value under the cursor instead of H/L.

`Bus` channels render as a **hex-banded trace** instead of `PlotStairs` —
one filled+outlined rectangle per run of consecutive equal-valued samples,
so the rectangle edges land exactly at value-change points, with the hex
value (`X2`/`X4`, sized off `BitWidth`) centered in the rectangle when
there's room for it (skipped below some width, per `ImGui.CalcTextSize`).
This is custom draw-list code, not an ImPlot mark — ImPlot has no built-in
"bus" primitive (see Open risks, resolved) — done via
`ImPlot.GetPlotDrawList()` returning an `ImDrawListPtr`, and
`ImPlot.PlotToPixels(x, y)` to convert plot-space coordinates (sample
index, and a fixed band of y ∈ [0.15, 0.85]) into the screen pixels that
draw-list calls need. The whole per-row draw loop is wrapped in
`ImPlot.PushPlotClipRect()`/`PopPlotClipRect()`, which both keeps rects
from bleeding outside the row's plot and makes it safe to let the last
run's right edge run one sample past the visible window (avoids that
run — often just one sample wide, right at "now" — collapsing to a
zero-width band). Colors come from the `PlotHistogram` theme slot
(`ImGui.GetColorU32(ImGuiCol.PlotHistogram[, alpha])`) rather than a
hardcoded color, so it tracks the active ImGui theme.

#### Layout false starts

Worth reading before touching this again — the render structure went
through two rejected designs before landing on the current one:

1. **One shared `ImPlot` plot, every channel via repeated `PlotDigital`
   calls** (ImPlot's own digital-signal auto-stacking, with its legend
   disabled). Compiled and ran fine, but there was no visible link between
   a channel's name (in a separate sidebar) and which stacked band was
   its data — rejected on review.
2. **`ImPlot.BeginSubplots`, one row per channel, each row's `BeginPlot`
   titled with the channel name.** Fixed the "which band is which"
   problem — each row now had its own title — but the title sits *above*
   the row's plot, not to its left, which wasn't the layout being asked
   for (a Saleae/Logic-style left-hand label column).
3. **Current: one `ImGui.BeginTable` (2 columns: Channel, Waveform), with
   `ScopeChannelGroup`s as tree rows** (`ImGui.TreeNodeEx(...,
   ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanAllColumns)`,
   the standard ImGui "tree in a table" pattern) **and each leaf channel
   as its own row: name in column 0, an independent `BeginPlot`/`EndPlot`
   sized into column 1.** This is what actually gives a left-hand label
   column regardless of tree nesting depth, since the table enforces a
   consistent column 0 width. The tradeoff: dropping Subplots also means
   dropping its automatic `LinkAllX` — see the Phase 3 note below.

### Channel model

```
ScopeChannelNode              (abstract: shared Name)
├─ ScopeChannel                (leaf: one sampled signal)
│    Kind: Digital | Bus
│    BitWidth: int             (1 for Digital, e.g. 8/16 for Bus)
│    Read: Func<ulong>         (digital reads back as 0/1)
└─ ScopeChannelGroup           (composite: ordered List<ScopeChannelNode>)
                                (renders as a collapsible tree row;
                                 collapsing only hides member rows, it
                                 doesn't stop recording them)
```

Keeping the sample type as a single `ulong` regardless of `Kind` lets the
recorder and ring buffer stay generic — `Kind`/`BitWidth` only matter to
the renderer (step-line vs. hex band). There's no per-channel show/hide
any more (removed on review — see Window layout below): every channel is
always recorded and always rendered, so `Kind`/`BitWidth` are the only
thing the UI branches on.

### Recording

`ScopeRecorder` is constructed from a flattened list of leaf
`ScopeChannel`s and owns one fixed-depth ring buffer (`ulong[]`) per
channel, plus a shared write cursor. `Sample()` reads every channel's
`Read()` and appends. Capacity is a tunable constant to start (a few
hundred thousand samples costs low tens of MB total across a dozen
channels — cheap; not a design axis worth locking down now).

To drive `Sample()` once per tick, `Debugger` gets a small hook:

```csharp
public event Action? Ticked;   // raised in RunForDuration, right after TickSystem()
```

`OscilloscopeWindow` subscribes in its constructor and no-ops inside the
handler when `!IsOpen` — same "skip when not open" convention
`DebuggerWindow.Prepare`/`Draw` already use, so there's no separate
subscribe/unsubscribe lifecycle to manage.

### Window layout

One merged view — no separate sidebar. A single `ImGui.BeginTable` (2
columns: "Channel", "Waveform") holds the whole tree:

- `ScopeChannelGroup`s are tree rows (`TreeNodeEx(...,
  ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanAllColumns)`),
  expanded by default, spanning both columns.
- `ScopeChannel` leaves are rows with the name in column 0 and an
  independent `ImPlot` filling column 1: `Digital` channels as a
  step-line plot, `Bus` channels as a hex-banded trace (see Rendering
  above for both).
- No per-channel show/hide control — removed on review in favor of
  "just show everything," since the channel counts so far (a dozen or
  so) don't need it. Worth revisiting if/when channel lists grow large
  enough that scrolling past unwanted ones becomes annoying.
- No toolbar yet. Zoom is still fixed (Phase 3), and there's no time
  ruler on screen yet either — see the Phase 3 section for what's still
  needed there.

### Chips own their channel group; systems compose

Mirroring the existing `internal void CreateDebuggerWindows(List<DebuggerWindow> result)`
method already on `Mos6502Chip` (called by `AppleIIDebugger` as
`_appleII.Cpu.CreateDebuggerWindows(result)`), each CPU chip gets a
matching `internal ScopeChannelGroup CreateScopeChannelGroup()`, defined
once on the chip itself and reused by every system that embeds it. Today
that's just `AppleIISystem`, but a `Mos6502Chip` is also used elsewhere
in the codebase (e.g. `Atari2600System`'s `Mos6507`, `Nes`) — without this,
each of those systems would otherwise redefine the same Address/Data/RW/...
channel list. This is why the request came up: **the CPU class owns its
own pins, once.**

A system then composes its root group from its chip(s) plus whatever
system-level glue signals it wants to add — e.g. on `AppleIISystem`:

```csharp
internal ScopeChannelGroup CreateScopeChannelGroup()
{
    return new ScopeChannelGroup("Apple II",
    [
        Cpu.CreateScopeChannelGroup(),
        new ScopeChannelGroup("Video Timing",
        [
            ScopeChannel.Digital("HBL", () => Hbl),
            ScopeChannel.Digital("VBL", () => Vbl),
            ScopeChannel.Digital("Color Burst Gate", () => ColorBurstGate),
            ScopeChannel.Digital("Phase 0", () => Phase0),
        ]),
    ]);
}
```

`AppleIIDebugger.CreateDebuggerWindows` then just does
`result.Add(new OscilloscopeWindow(this, _appleII.CreateScopeChannelGroup()));`,
same shape as how it already assembles `BreakpointsWindow`/`MemoryEditor`/
`ScreenDisplayWindow` today.

**Starter list for Apple II** (everything below is already a public
member — no new pin exposure needed to get started):

| Group | Channel | Kind | Source | Owned by |
|---|---|---|---|---|
| MOS6502 | Address | Bus (16-bit) | `Address` | `Mos6502Chip.CreateScopeChannelGroup()` |
| MOS6502 | Data | Bus (8-bit) | `Data` | `Mos6502Chip.CreateScopeChannelGroup()` |
| MOS6502 | R/W | Digital | `RW` | `Mos6502Chip.CreateScopeChannelGroup()` |
| MOS6502 | SYNC | Digital | `Sync` | `Mos6502Chip.CreateScopeChannelGroup()` |
| MOS6502 | RDY | Digital | `Rdy` | `Mos6502Chip.CreateScopeChannelGroup()` |
| MOS6502 | IRQ | Digital | `Irq` | `Mos6502Chip.CreateScopeChannelGroup()` |
| MOS6502 | NMI | Digital | `Nmi` | `Mos6502Chip.CreateScopeChannelGroup()` |
| MOS6502 | PHI2 | Digital | `Phi2` | `Mos6502Chip.CreateScopeChannelGroup()` |
| Video Timing | HBL | Digital | `AppleIISystem.Hbl` | `AppleIISystem.CreateScopeChannelGroup()` |
| Video Timing | VBL | Digital | `AppleIISystem.Vbl` | `AppleIISystem.CreateScopeChannelGroup()` |
| Video Timing | Color Burst Gate | Digital | `AppleIISystem.ColorBurstGate` | `AppleIISystem.CreateScopeChannelGroup()` |
| Video Timing | Phase 0 | Digital | `AppleIISystem.Phase0` | `AppleIISystem.CreateScopeChannelGroup()` |

More glue signals (e.g. the address-decoder chip outputs currently held
as private fields on `AppleIISystem`) can be added later by bumping the
relevant field to `internal`/`public`, same incremental pattern the rest
of the debugging UI already follows — not a blocker for the starter list.
Other chips (`Intel8080`, `Ricoh2C02`, ...) can grow their own
`CreateScopeChannelGroup()` the same way, on their own schedule — nothing
about the framework requires every chip to have one from day one.

## Phased plan

**Phase 0 — Scaffolding**
Add the `Hexa.NET.ImPlot` 2.2.9 package reference. `ScopeChannel`/
`ScopeChannelGroup` types, `ScopeRecorder` ring buffer, `Debugger.Ticked`
event, `Mos6502Chip.CreateScopeChannelGroup()` and
`AppleIISystem.CreateScopeChannelGroup()` per the composition above, and a
minimal `OscilloscopeWindow` that just lists channel names (no waveform
drawing yet) to prove the plumbing end to end. Wire into `AppleIIDebugger`.

**Phase 1 — Digital waveform rendering (done)**
Landed as: one `ImGui` table (channel name column + waveform column),
`ScopeChannelGroup`s as expanded-by-default tree rows spanning both
columns, `Digital` channels as their own `PlotStairs` step-line plot with
"L"/"H" y-axis ticks and a per-row hover tooltip. Went through two other
layouts first — see "Layout false starts" above. Right-anchored to "now"
while the debugger runs, holding still when it's stopped, for free, same
mechanism as originally planned. No show/hide control (removed on
review — see Window layout).

**Phase 2 — Bus channel rendering (done)**
Landed as: one filled+outlined rectangle per run of equal samples (edges
at value-change points by construction), drawn via
`ImPlot.GetPlotDrawList()`/`PlotToPixels()` since ImPlot has no built-in
bus mark, with the hex value centered in the rectangle when it fits. Hover
tooltip reuses the `IsPlotHovered`/`GetPlotMousePos`/`SetTooltip` pattern
from digital rows, with `Math.Floor` instead of `Math.Round` for the
nearest-sample lookup (a bus value spans `[i, i+1)`, not a point at `i`)
and the value formatted as hex instead of H/L. See Rendering above for
the rest of the detail (clip rect, color source, last-run-width handling).

**Phase 3 — Timescale controls (done)**
Landed as: the x-axis is absolute sample-index units throughout (one tick
= one `Debugger.Ticked` call, i.e. one `ScopeRecorder.Sample()` call), not
a window-relative 0-based index like Phases 1/2 used. `OscilloscopeWindow`
holds a shared `_viewMin`/`_viewMax` pair (absolute sample-index units)
that every row's independent `BeginPlot` reads/writes identically each
frame via `SetupSharedXAxis`, which is what keeps rows in sync without
`ImPlot.BeginSubplots` (Phase 1 dropped Subplots, and with it
`ImPlotSubplotFlags.LinkAllX` — see "Layout false starts"):

- **While the debugger is running:** `_viewMin`/`_viewMax` are recomputed
  every frame as a fixed-width window ending at the live edge and forced
  onto each row via `SetupAxisLimits(X1, _viewMin, _viewMax,
  ImPlotCond.Always)`. No interaction while running, same as before.
- **While stopped:** `SetupSharedXAxis` instead calls
  `SetupAxisLimitsConstraints(X1, oldestRetained, now)` (clamps panning to
  what the ring buffer still retains), `SetupAxisZoomConstraints(X1, 2,
  retainedRange)` (can't zoom tighter than 2 samples), and
  `SetupAxisLinks(X1, ref _viewMin, ref _viewMax)` — this last call is
  what both reads the shared range into the row's axis *and* writes back
  whatever the user just dragged/scroll-zoomed, so the next row drawn the
  same frame (and every row next frame) picks up the same range. This is
  the explicit axis-linking the note below used to warn about.
- The first frame after the debugger stops (or a "Jump to Now" button
  above the table is clicked) snaps `_viewMin`/`_viewMax` back to the
  live-edge-anchored default window, then leaves it alone so the user's
  subsequent pan/zoom sticks.
- **The time ruler is drawn exactly once**, not per-row — a first review
  pass flagged that per-row tick labels were visual clutter once every row
  shares the same axis range anyway. `DrawTimescaleRow` renders it as a
  zero-data `ImPlot` (just axis chrome, `SetupAxisFormat(X1,
  _timeAxisFormatter)`) occupying row 0 of the same `Channel`/`Waveform`
  table the channels use, with `ImGui.TableSetupScrollFreeze(0, 1)`
  freezing that row so it stays pinned above the channel rows during
  vertical scroll — normal ImGui table header-freeze behavior, not
  anything oscilloscope-specific. Being the same table column guarantees
  its pixel width (and thus tick alignment) matches the channel rows
  exactly. Channel rows keep `ImPlotAxisFlags.NoTickLabels` on X and no
  longer call `SetupAxisFormat` at all.
- `FormatTimeAxisTick` (the `ImPlotFormatter` callback backing the ruler)
  converts the sample index to seconds via `CyclesPerSecond` and picks
  s/ms/us/ns based on magnitude, reused for `FormatDuration`'s console-facing
  string too.
- **Zoom control**, Saleae Logic-style: a toolbar row above the table has
  "Jump to Now", "-"/"+" buttons, and an editable "ms / 100px" textbox
  (`ImGui.InputText` bound to `_zoomInputBuffer`, refreshed from
  `_millisecondsPer100Px` only while the field isn't focused, so typing
  isn't clobbered mid-edit). `_millisecondsPer100Px` is the source of
  truth for window width whenever the toolbar drives a change (live
  follow, "Jump to Now", a button click, or a committed textbox edit);
  otherwise (an ordinary stopped frame with no toolbar interaction) it's
  resynced *from* `_viewMin`/`_viewMax` so it reflects whatever the user
  just dragged or scroll-zoomed directly on a row — two-way binding
  between the textbox and the interactive axes. That resync is skipped
  while `TotalSamples == 0`: with no data yet, `axisUpperBound` is padded
  to a degenerate 1-sample range just to keep the axis calls well-formed,
  and reading that back corrupted the zoom readout to ~0 before real data
  existed (caught by launching the app and checking the toolbar, not by
  reading the code — worth remembering that this class's arithmetic is
  easy to get subtly wrong in ways that only show up on screen).
- Sample fetch (`FillVisibleSamples`) and the bus-band draw loop
  (`DrawBusTrace`) both moved from a fixed window-relative index to
  reading `ImPlot.GetPlotLimits(X1)` per row per frame and mapping
  absolute sample index → ring buffer slot via plain `% Capacity` (no
  negative-wraparound handling needed, since absolute indices are always
  ≥ 0) — this is what lets a row actually show anything other than the
  fixed trailing window Phases 1/2 hardcoded.

**Phase 4 — Polish (done, minus cursors)**
Landed group-collapse persistence and per-channel trace colors; measurement
cursors were left as the still-open stretch item (no design work done here —
deferred rather than attempted and cut).

- **Group collapse persistence** generalizes the existing `ImGuiSettingsHandler`
  wiring in `Program.cs`, which previously only ever wrote/read a hardcoded
  `IsOpen=1` line per window. `DebuggerWindow` gained two virtual hooks,
  `GetPersistedSettingsLines()` / `ApplyPersistedSettingsLine(string)`, so any
  window can persist its own extra state through the same `[Aemula][<window
  name>]` ini section without `Program.cs` needing to know what that state
  means. `ImGuiSettingsWriteAll` appends each window's extra lines after
  `IsOpen=1`; `ImGuiSettingsReadLine` forwards any line that isn't `IsOpen=1`
  to `ApplyPersistedSettingsLine`.
- `OscilloscopeWindow` tracks collapsed groups as a `HashSet<string>` of
  `/`-joined name paths from the root (e.g. `"Apple II/MOS6502"`), serialized
  as one `CollapsedGroups=path;path;...` line. Absence from the set means
  expanded, matching the existing default. Each group row applies its
  persisted state via `ImGui.SetNextItemOpen(open, ImGuiCond.FirstUseEver)`
  before `TreeNodeEx`, then mirrors `TreeNodeEx`'s return back into the set
  every frame — so a live toggle both drives that frame's rendering and is
  what gets written out next time the ini saves. No escaping of `/` or `;` in
  group names — not needed for the names in use today, worth revisiting if a
  group name ever needs either character.
- **Per-channel trace colors** replace the flat `ImGuiCol.PlotHistogram`
  theme color every bus channel shared (and digital rows' unstyled default,
  which was invisible as a distinguishing feature since each row is its own
  independent `BeginPlot` and so always restarts ImPlot's color cycle at the
  same first color). Each leaf channel now gets
  `ImPlot.GetColormapColor(channelIndex, ImPlotColormap.Deep)` — a
  deterministic, theme-independent color keyed to the channel's position in
  the flattened channel list (the same index `_channelIndex` already tracked
  for buffer lookups), wrapping automatically if the channel count exceeds
  the colormap's size. Digital rows apply it via
  `ImPlot.PushStyleColor(ImPlotCol.Line, color)` /`PopStyleColor()` around
  `PlotStairs`; bus rows convert it to fill (35% alpha) and border `ImU32`
  via `ImGui.GetColorU32` instead of reading the `PlotHistogram` theme slot.
  No UI to customize colors and nothing persisted — this is the "auto-assigned
  palette" option from the two considered when this phase started, chosen
  over a persisted per-channel color picker since it needed no new UI surface
  and still solves the actual problem (channels being visually
  indistinguishable from each other).

**Phase 5 (stretch, later) — Analog channels**
Add the `Analog` channel kind once composite video produces a real signal to
sample. Not started until that groundwork exists — now planned in detail as
its own phase in `docs/apple-ii-ntsc-video-plan.md`, which also found this
doesn't need a new float sample type after all: the encoder's byte-valued
signal fits the existing `ulong`-per-sample model, so `Analog` ends up being
a rendering distinction (a continuous line trace), not a storage one.

**Phase 6 (stretch, later) — More systems**
Same framework is already system-agnostic by Phase 0 — add a system-level
`CreateScopeChannelGroup()` (composing that system's chips, most of which
already have one by this point) + one line in the relevant system's
`Debugger.CreateDebuggerWindows`, for NES/Chip8/Atari2600/etc. The
`Atari2600`'s `Mos6507` and `Nes`'s CPU can both reuse
`Mos6502Chip.CreateScopeChannelGroup()` directly if they wrap the same
chip class — worth checking when this phase starts.

## Open risks

- ~~This is the project's first use of ImPlot~~ — resolved: Phase 1 spiked
  it (see "Layout false starts" above). The API doesn't map 1:1 onto the
  hand-rolled `ImDrawList` patterns elsewhere in the codebase, and its
  "obvious" digital-signal feature (`PlotDigital`'s auto-stacking) turned
  out to be the wrong tool for a labeled-row layout — worth remembering
  before reaching for another ImPlot "does this for you" feature without
  checking it actually fits.
- Ring buffer capacity is still a guessed starting constant (`ScopeRecorder.DefaultCapacity`);
  now that Phase 1 is actually on screen, it's worth revisiting whether
  it's too short to be useful zoomed out, or larger than it needs to be.
- ~~Bus-band rendering (Phase 2) is the fiddliest drawing code here~~ —
  resolved: landed as per-run filled/outlined rectangles via
  `GetPlotDrawList()`/`PlotToPixels()` (see Rendering above), used
  directly for both the 8-bit Data bus and 16-bit Address bus rather than
  prototyping one width first. Still needs a manual look in-app (narrow
  1-2 sample runs at high activity, and the 16-bit Address column at the
  default row height) before calling the visual result settled.
- `Debugger.Ticked` fires every tick the emulator executes, including
  fast-forwarded/non-stepped runs — if that turns out to add measurable
  overhead even with `OscilloscopeWindow` closed (it shouldn't, since the
  handler no-ops on `!IsOpen`, but worth a sanity check once real channel
  counts exist), it's an easy escape hatch to only subscribe while open
  instead of always-subscribed-but-no-op.
- Phase 3's per-row sample fetch (`FillVisibleSamples`) allocates a fresh
  heap array once the visible range exceeds 4096 samples (same threshold
  Phases 1/2 used, just hit far more often now that zooming out while
  stopped can show the full ring buffer instead of a fixed pixel-width
  window) — one `new double[]` pair per channel per frame while zoomed
  out. Not addressed here since it's debug-UI-only and no stutter was
  observed manually, but worth pooling/reusing the buffers if it turns out
  to matter with more channels or a larger `DefaultCapacity`.
