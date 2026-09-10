using System;
using System.Collections.Generic;
using Aemula.Emulation.Systems.AppleI;
using Aemula.Emulation.Systems.AppleII;
using Aemula.Emulation.Systems.Atari2600;
using Aemula.Emulation.Systems.Nes;
using Aemula.Emulation.Systems.SpaceInvaders;

namespace Aemula.Emulation.Systems;

// One emulated machine the emulator can boot end-to-end: its canonical id (CLI
// arg, --input target, UI System submenu), display name, the expansion slots it
// exposes for configuration, and how to construct the system for a chosen slot
// configuration. Build turns that into a whole Rig - the system plus every
// cabled peripheral it or its fitted cards need - via the generic RigBuilder,
// so nothing here names a concrete peripheral type.
public sealed record SystemDescriptor(
    string Id,
    string DisplayName,
    IReadOnlyList<ExpansionSlot> Slots,
    Func<ExpansionSlotConfiguration, EmulatedSystem> CreateSystem)
{
    public Rig Build(ExpansionSlotConfiguration slotConfiguration) =>
        RigBuilder.Build(CreateSystem, slotConfiguration, Slots);
}

// Single source of truth for which emulated systems are wired up end-to-end.
// BbcMicro and AcornSystem1 aren't listed - they're not yet complete enough to
// boot from any of these entry points.
//
// Aemula.Console, Aemula.UI and Aemula.Benchmarks each need more than an
// id/factory pair per system (a ROM-picker filter, a benchmark workload) -
// they key their own per-id tables off Id rather than re-declaring the
// id/factory list themselves.
public static class EmulatedSystems
{
    public static readonly IReadOnlyList<SystemDescriptor> All =
    [
        new("appleii", "Apple II+", [], static _ => new AppleIISystem()),

        // As an owner would have equipped it to run BASIC: the ACI cassette
        // card fitted by default (its DefaultCardId), and the second RAM bank
        // jumpered to $E000 where Integer BASIC loads. `--slot expansion=none`
        // and AppleISystemOptions.Default give a bare board.
        new("applei", "Apple I", AppleIExpansionSlots.Catalog,
            static slots => new AppleISystem(AppleISystemOptions.EquippedForBasic, slots)),

        new("atari2600", "Atari 2600", [], static _ => new Atari2600System()),
        new("nes", "NES", [], static _ => new NesSystem()),
        new("spaceinvaders", "Space Invaders", [], static _ => new SpaceInvadersSystem()),
    ];

    public static SystemDescriptor? FindById(string? id)
    {
        if (id == null)
        {
            return null;
        }

        foreach (var descriptor in All)
        {
            if (descriptor.Id == id)
            {
                return descriptor;
            }
        }

        return null;
    }
}
