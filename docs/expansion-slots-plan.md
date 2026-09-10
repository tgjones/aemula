# Expansion Slots — Implementation Plan

## Goal

A generic, system-agnostic API for **configuring expansion cards in slots** —
the same idea MAME calls slots and slot options. The command line and the UI
should let a user say "this card in this slot, or nothing in this slot" for any
system, with no per-system configuration code: the arg parsing, menu building,
validation, and `--list-slots` output are all generic.

Two things stay per-system and are expected to:

- **The pin-level card.** `Aemula.Emulation.Systems.AppleI.IExpansionCard` is
  the right shape for "what a card is, electrically." The Apple II slot is a
  different connector (50-pin, `DEVICE SELECT` / `I/O SELECT` / `I/O STROBE` /
  `INH` / `DMA`) and gets its own `AppleII.IExpansionCard` — these are genuinely
  different electrical interfaces and do not share a base.
- **Binding a card id to a concrete card instance.** `"aci"` →
  `new AppleCassetteInterfaceCard()` is one line of system-owned code, sitting
  next to the slot declaration so there is a single source with no drift.

Everything between those two — enumerating slots, choosing cards, wiring up any
peripherals the card needs, assembling the `Rig` — becomes generic.

Non-goals for this pass are listed under [Out of scope](#out-of-scope).

## Current state

- **`AppleI.IExpansionCard`** — pin-level, `set`-only connector inputs, nullable
  outputs for open-collector lines, `Phi2` setter does the per-cycle work.
- **`AppleISystemOptions`** — construction-time `readonly struct`,
  `bool CassetteCard` + `bool RamExpansionAtE000`.
- **`AppleISystem`** holds one `IExpansionCard? _expansionCard`, driven once per
  CPU bus cycle in `DoCpuMemoryAccess`.
- **`EmulatedSystems.All`** — `SystemDescriptor(Id, DisplayName, Func<Rig> Build)`.
  `BuildAppleI` hardcodes the options **and** creates the `CassetteDeck`
  peripheral, patches it to the ACI card's jacks, and passes it to the `Rig`.
- **`SystemCatalog`** (`Aemula.UI`) — layers ROM-picker metadata onto
  `EmulatedSystems.All`, keyed by `Id`; drives the `File > System` submenu. This
  is the established "generic registry in core + presentation metadata in the
  UI" pattern the slot menu will follow.
- **`Rig`** — plain composition of system + peripherals; aggregates console
  controls and audio; "never names a concrete peripheral type."

All of the above except `Aemula.UI` / `Aemula.Console` / `Aemula.Benchmarks`
lives in the one `Aemula` assembly, so the new generic types are a
namespace-placement decision, not a project one.

## Design

Six layers, from the metal up. Layers 2–5 are generic; layer 6 is the small
system-owned binding.

### 1. Pin-level card — unchanged

`AppleI.IExpansionCard` as it is today. Apple II gets its own later.

### 2. Slot catalog — generic capability data

Immutable description of what a system *can* take. No system instance needed to
read it (the CLI validates `--slot` args and prints `--list-slots` with no side
effects), so it is a `static` on the system type, referenced by the descriptor.

```csharp
// src/Aemula/Emulation/Systems/ExpansionSlot.cs
namespace Aemula.Emulation.Systems;

public sealed record ExpansionSlot(
    string Id,                               // "expansion" (Apple I); "slot1".."slot7" (Apple II)
    string DisplayName,                      // "Expansion connector" / "Slot 3"
    IReadOnlyList<ExpansionCardOption> Cards,
    string? DefaultCardId = null);           // null => empty unless the user picks a card

public sealed record ExpansionCardOption(
    string Id,                               // "aci", "disk-ii"
    string DisplayName,                      // "Apple Cassette Interface"
    string? Summary = null);                 // one-liner for --list-slots / UI tooltip
```

The empty choice is implicit — always allowed, never listed as a card.

### 3. Slot configuration — the user's choice

Produced by CLI parsing and the UI menu, validated against the catalog.

```csharp
// src/Aemula/Emulation/Systems/ExpansionSlotConfiguration.cs
public sealed class ExpansionSlotConfiguration
{
    // Built from (slotId, cardId?) pairs — the CLI accumulates them from
    // repeated --slot args, the UI menu emits one on each pick. A null cardId
    // means "force this slot empty". Order is preserved but not significant;
    // a repeated slotId is last-wins.
    public ExpansionSlotConfiguration(IEnumerable<(string SlotId, string? CardId)> choices);

    public static ExpansionSlotConfiguration Empty { get; }   // no choices — every slot takes its default

    // null => empty slot (either an explicit =none, or no default and no choice)
    public string? this[string slotId] { get; }

    // Fills unspecified slots with their DefaultCardId; throws on an unknown
    // slot id or a card id not offered by that slot.
    public ExpansionSlotConfiguration ResolvedAgainst(IReadOnlyList<ExpansionSlot> slots);
}
```

### 4. Peripheral requests — cards (and systems) declare what they need

The ACI needs a `CassetteDeck`. The only genuinely bespoke steps for an
`(ACI, CassetteDeck)` pair are *build the device* (needs the Apple-I φ2 divisor
of 14) and *cable it* (`aci.CassetteInput = deck.ReadPlayback`). So a request
carries both, plus the attach — "what kind" and "how to build" are the same
thing, and no `enum PeripheralKind` is needed.

```csharp
// src/Aemula/Emulation/Peripherals/PeripheralRequest.cs
namespace Aemula.Emulation.Peripherals;

public sealed class PeripheralRequest
{
    public Func<PeripheralContext, IPeripheral> Create { get; }
    public Action<IPeripheral> Attach { get; }

    public static PeripheralRequest For<T>(
        Func<PeripheralContext, T> create,
        Action<T> attach) where T : class, IPeripheral
        => new(ctx => create(ctx), p => attach((T)p));
}

public sealed record PeripheralContext(EmulatedSystem System);   // exposes CyclesPerSecond, etc.
```

- **Systems declare directly** via a base hook, for motherboard-level cabled
  devices (the Apple II's own cassette port, a printer port):

  ```csharp
  // EmulatedSystem
  public virtual IReadOnlyList<PeripheralRequest> PeripheralRequests => [];
  ```

- **Cards declare** via their installation bundle (layer 6). A slotted system's
  `PeripheralRequests` returns its own plus every installed card's, concatenated.

If two installed cards ever request the same peripheral *type*, this builds two
instances. That is fine for every case that exists today; a shared-device notion
is [out of scope](#out-of-scope).

### 5. The descriptor seam + one generic `Rig` builder

```csharp
public sealed record SystemDescriptor(
    string Id,
    string DisplayName,
    IReadOnlyList<ExpansionSlot> Slots,                       // [] for a slotless system
    Func<ExpansionSlotConfiguration, EmulatedSystem> CreateSystem)
{
    public Rig Build(ExpansionSlotConfiguration config) => RigBuilder.Build(CreateSystem, config, Slots);
}
```

```csharp
public static class RigBuilder
{
    public static Rig Build(
        Func<ExpansionSlotConfiguration, EmulatedSystem> create,
        ExpansionSlotConfiguration config,
        IReadOnlyList<ExpansionSlot> slots)
    {
        var system = create(config.ResolvedAgainst(slots));
        var ctx = new PeripheralContext(system);
        var peripherals = system.PeripheralRequests
            .Select(r => { var p = r.Create(ctx); r.Attach(p); return p; })
            .ToList();
        return new Rig(system, peripherals);
    }
}
```

`BuildAppleI` — with its bespoke option struct *and* its hand-written
`CassetteDeck` patching — **is deleted**. There is no per-system `Rig` assembly
code left; the only per-system code is the system constructor and the card
binding.

Peripherals are created *after* `new AppleISystem(...)` returns, so
`PeripheralContext.System.CyclesPerSecond` is fully available; the system
constructor only records the requests.

### 6. System-side binding — the one small per-system table

Lives entirely inside the system's own namespace; never escapes it. The card
interface is per-system, so the binding/installation types are generic on it to
avoid duplicating them per system:

```csharp
// src/Aemula/Emulation/Systems/ExpansionCardBinding.cs  (generic on the card interface)
public sealed record ExpansionCardBinding<TCard>(
    ExpansionCardOption Option,
    Func<ExpansionCardInstallation<TCard>> Install);

public sealed record ExpansionCardInstallation<TCard>(
    TCard Card,
    IReadOnlyList<PeripheralRequest> Peripherals);
```

Apple I's catalog + bindings:

```csharp
// AppleISystem.ExpansionSlots.cs
internal static class AppleIExpansionSlots
{
    public const string ExpansionSlotId = "expansion";
    public const string AciCardId = "aci";

    public static readonly IReadOnlyList<ExpansionCardBinding<IExpansionCard>> ExpansionBindings =
    [
        new(new ExpansionCardOption(AciCardId, "Apple Cassette Interface",
                "Woz's cassette card: firmware at $C100, tape in/out jacks."),
            Install: () =>
            {
                var card = new AppleCassetteInterfaceCard();
                return new ExpansionCardInstallation<IExpansionCard>(card,
                [
                    PeripheralRequest.For<CassetteDeck>(
                        ctx  => new CassetteDeck(ctx.System.CyclesPerSecond / 14.0),
                        deck => { card.CassetteInput = deck.ReadPlayback; card.CassetteOutput = deck.WriteCapture; }),
                ]);
            }),
    ];

    // Projected into the descriptor. DefaultCardId = "aci": `applei` boots
    // equipped, matching today's BuildAppleI ("as an owner would have equipped
    // it"). A bare board is `--slot expansion=none`.
    public static readonly IReadOnlyList<ExpansionSlot> Catalog =
    [
        new ExpansionSlot(ExpansionSlotId, "Expansion connector",
            [.. ExpansionBindings.Select(b => b.Option)],
            DefaultCardId: AciCardId),
    ];
}
```

The `AppleISystem` constructor takes a `ExpansionSlotConfiguration`, and for each slot:
look up `config[slot.Id]`, find the matching binding, call `Install()`, plug
`.Card` into the bus array, and fold `.Peripherals` into the list returned by
`PeripheralRequests`.

## New / changed types

| Type | File | Notes |
|---|---|---|
| `ExpansionSlot`, `ExpansionCardOption` | `src/Aemula/Emulation/Systems/ExpansionSlot.cs` | new, generic |
| `ExpansionSlotConfiguration` | `src/Aemula/Emulation/Systems/ExpansionSlotConfiguration.cs` | new, generic |
| `ExpansionCardBinding<TCard>`, `ExpansionCardInstallation<TCard>` | `src/Aemula/Emulation/Systems/ExpansionCardBinding.cs` | new, generic on the per-system card interface |
| `PeripheralRequest`, `PeripheralContext` | `src/Aemula/Emulation/Peripherals/PeripheralRequest.cs` | new, generic |
| `EmulatedSystem.PeripheralRequests` | `src/Aemula/EmulatedSystem.cs` | new `virtual`, default `[]` |
| `SystemDescriptor` | `src/Aemula/Emulation/Systems/EmulatedSystems.cs` | gains `Slots`; `Build` → `Build(ExpansionSlotConfiguration)`; factory → `Func<ExpansionSlotConfiguration, EmulatedSystem>` |
| `RigBuilder` | `src/Aemula/Emulation/Systems/RigBuilder.cs` | new; the single generic assembler |
| `EmulatedSystems.BuildAppleI` | — | **deleted** |
| `AppleISystemOptions.CassetteCard` | `.../AppleI/AppleISystemOptions.cs` | **removed** — the ACI comes from `ExpansionSlotConfiguration` now |
| `AppleIExpansionSlots` | `.../AppleI/AppleISystem.ExpansionSlots.cs` | new; catalog + bindings |
| `AppleISystem` ctor / `_expansionCard` | `.../AppleI/AppleISystem.cs` | takes `ExpansionSlotConfiguration`; `_expansionCard` → `IExpansionCard?[] _slots` (one entry today) |
| `SystemCatalog` / `EmulationWindow` | `Aemula.UI` | Slots submenu (below) |
| `Program.ParseArgs` | `Aemula.Console` | `--slot`, `--list-slots` (below) |

## CLI surface (generic)

- `--slot <slotId>=<cardId>` — repeatable. `--slot <slotId>=none` forces the
  slot empty (overriding its `DefaultCardId`).
- `--list-slots <system>` — prints each slot, its cards (`Id` — `DisplayName` —
  `Summary`), and which is the default. No system is run.
- Unspecified slots take `DefaultCardId`. So plain `--system applei` boots the
  equipped machine (ACI fitted); `--system applei --slot expansion=none` is a
  bare board.
- Validation errors (unknown slot id, card not offered by that slot) surface
  through `Program`'s existing single top-level catch.
- `Program.Run` builds the `ExpansionSlotConfiguration` from the parsed pairs and calls
  `descriptor.Build(config)`. `InputScript.Parse` still runs against the built
  `Rig` (unchanged — the cassette deck is still a peripheral with mnemonics).

## UI surface (generic)

- A **Slots** submenu (under `File`, beside `System`), shown only when the
  current system's `Slots` is non-empty.
- One nested menu per slot: a radio list of `(empty)` + each card, the current
  selection checked.
- Picking an entry rebuilds the `Rig` with the updated `ExpansionSlotConfiguration` —
  the exact mechanism `EmulationWindow` already uses to switch systems.
- The slot *data* comes from the descriptor; `SystemCatalog` just renders it,
  symmetrically with how it renders ROM-picker metadata today. `SystemCatalog`
  gains no new per-system table for this.

## Benchmarks

`SystemSpecs` / `ProfileHarness` build systems directly; they pass
`ExpansionSlotConfiguration.Empty` (resolves to each slot's default) or an explicit
config where a benchmark wants one. Mechanical change only.

## Related change: Apple I RAM two-bank model

Not a slot — the `$E000` RAM on a real Apple I was **onboard jumpers, not a
card** — but the current option model is wrong and should be tightened in the
same pass.

**Hardware.** The board has two 4K DRAM banks. The jumper block maps each bank
independently. Bank A sits at `$0000–$0FFF`. Bank B is jumpered to **either**
`$1000–$1FFF` (contiguous 8K) **or** `$E000–$EFFF`. The common configuration
for running Cassette BASIC was bank A at `$0000` + **bank B at `$E000`**, with
the ACI in the single connector. You could not have 8K contiguous *and* 4K at
`$E000` from onboard RAM — only two banks exist. More than that needed a RAM
card on the connector, which then displaced the ACI.

**Current model (wrong).** `_ram = new byte[0x2000]` always covers
`$0000–$1FFF`, *plus* an optional separate `_ramExpansion` `byte[0x1000]` at
`$E000` — up to 12K, impossible on a real single-connector machine.

**Change.**

- Replace `AppleISystemOptions.RamExpansionAtE000` with a bank-B mapping,
  e.g. `enum BankBMapping { At1000, AtE000 }` (or a `bool bankBAtE000`).
- Model bank B as always populated (matches the existing "both onboard MK4096
  banks populated" comment); an unpopulated bank B is a possible later
  refinement, not now.
- `ReadByte` / `WriteByte`: bank A serves `!Y0`; bank B serves `!Y1` when
  `At1000`, or `!Y14` when `AtE000`. When bank B is at `$E000`, `$1000–$1FFF`
  is open bus; when at `$1000`, `$E000–$EFFF` is open bus.
- `EmulatedSystems` default for `applei` → bank B at `$E000` (equipped BASIC
  machine). `AppleISystemOptions.Default` (bare board) → bank B at `$1000`.
- Update `AppleISystemTests`:
  `E000BlockIsOpenBusWithoutTheRamExpansion` → open bus when bank B is at
  `$1000`; `RamExpansionAtE000IsReadWriteWhenFitted` → bank B mapped to `$E000`
  makes `$E000` R/W **and** `$1000` open bus.

The generic "expose DIP-switch-style options like we do expansion cards" idea
is [out of scope](#out-of-scope); this change keeps `AppleISystemOptions` as a
plain struct.

## Apple II sketch (not built here)

Confirms the abstraction fits without forcing it now. `AppleIISystem` already
notes its address decode reserves `H12` `Y1–Y7` for the seven slots'
`$C1XX–$C7XX` `I/O SELECT'` ranges ("not wired up — no slot cards yet"). Under
this design that becomes:

- `AppleIISlots.Catalog` = seven `ExpansionSlot`s, `slot1`…`slot7`, each
  `DefaultCardId: null` (Apple II shipped with empty slots).
- An `AppleII.IExpansionCard` pin interface (its own connector signals).
- Bindings for `disk-ii`, `language-card`, etc. as they are implemented.
- `DoCpuMemoryAccess` iterates the slot array with the fan-out it already has
  for one card.

No generic-layer change.

## Out of scope

- **Generic DIP-switch / jumper options.** A system-agnostic way to expose
  `AppleISystemOptions`-style toggles (bank-B mapping, Apple II revision, PAL/
  NTSC) in the CLI and UI, mirroring this slot API. Wanted eventually; not here.
- **Hot-plugging.** Construction-time only. Changing a card rebuilds the `Rig`,
  as switching systems already does — and matches treating a card cage with the
  power off. The slot abstraction does not preclude runtime plugging later.
- **Shared peripheral instances across cards.** Each `PeripheralRequest` builds
  its own device.
- **A shared pin-level card base interface.** Per-system card interfaces stay
  unrelated; generic plumbing is generic on `TCard`.

## Phases

1. **Generic types + seam, no behaviour change.** Add `ExpansionSlot`,
   `ExpansionCardOption`, `ExpansionSlotConfiguration`, `PeripheralRequest`,
   `PeripheralContext`, `RigBuilder`; reshape `SystemDescriptor`
   (`Slots = []` for all, `CreateSystem` ignores the config); add
   `EmulatedSystem.PeripheralRequests`. Move `BuildAppleI`'s `CassetteDeck`
   patching into an `AppleISystem.PeripheralRequests` override (ACI still built
   from the existing `bool` option). Delete `BuildAppleI`. All systems and
   tests green, no observable change.
2. **Apple I slot catalog.** Add `AppleIExpansionSlots` (catalog + `aci` binding with its
   `CassetteDeck` request). `AppleISystemOptions` loses `CassetteCard`;
   `AppleISystem` ctor takes an `ExpansionSlotConfiguration` and resolves the ACI from it.
   `EmulatedSystems.All` gives `applei` its `Slots` with `DefaultCardId: "aci"`.
   `AppleICassetteTests` (real `BASIC.wav` end-to-end via the descriptor) must
   pass unchanged.
3. **CLI.** `--slot`, `--list-slots`, defaulting, validation in `Aemula.Console`;
   tests for arg parsing and validation errors.
4. **UI.** Slots submenu in `SystemCatalog` / `EmulationWindow`; rebuild-on-change.
5. **Apple I RAM two-bank model.** Independent of 1–4; can land any time after
   phase 2. Option + `ReadByte`/`WriteByte` + test updates as above.

## Testing

- **Regression:** `AppleICassetteTests` (loads a real archive.org `BASIC.wav`
  through the ACI) via `EmulatedSystems.FindById("applei")!.Build(...)` must
  stay green — proves default slot resolution equips the ACI and its deck.
- **`ExpansionSlotConfiguration.ResolvedAgainst`:** unknown slot id throws; card not
  offered by the slot throws; unspecified slot takes `DefaultCardId`;
  `=none` overrides a non-null default.
- **`RigBuilder`:** a system with a card that has a `PeripheralRequest` yields a
  `Rig` whose `Peripherals` contains the built, attached device; a slotless
  system yields none.
- **CLI:** `--list-slots applei` output; `--slot expansion=none` produces a bare
  board (no `CassetteDeck` in the rig); bad `--slot` value exits non-zero with a
  message.
- **Apple I RAM:** updated `AppleISystemTests` for bank-B mapping (both
  positions, and the open-bus hole each leaves).

## Decisions settled

- **Naming.** `Expansion`-prefixed throughout, matching the existing
  `IExpansionCard`: `ExpansionSlot`, `ExpansionCardOption`,
  `ExpansionSlotConfiguration`, `ExpansionCardBinding<TCard>`,
  `ExpansionCardInstallation<TCard>`. (Not MAME's bare `slot` / `slot option`.)
- **`ExpansionSlotConfiguration`** is built from `(slotId, cardId?)` pairs and
  read through a `this[slotId]` indexer — no public dictionary.
- **`ExpansionCardBinding` / `ExpansionCardInstallation` are generic on
  `TCard`** (the per-system card interface), not duplicated per system.
  Revisit only if the generic constraint plumbing turns ugly.
