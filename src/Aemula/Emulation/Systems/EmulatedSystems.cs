using System;
using System.Collections.Generic;
using Aemula.Emulation.Peripherals;
using Aemula.Emulation.Peripherals.Cassette;
using Aemula.Emulation.Systems.AppleI;
using Aemula.Emulation.Systems.AppleII;
using Aemula.Emulation.Systems.Atari2600;
using Aemula.Emulation.Systems.Nes;
using Aemula.Emulation.Systems.SpaceInvaders;

namespace Aemula.Emulation.Systems;

// Build assembles a whole Rig: the system, and for a machine that had cabled
// peripherals, those devices patched to it. This is the one place that knows
// how a given machine's peripherals connect - the Rig itself never names a
// concrete peripheral type.
public sealed record SystemDescriptor(string Id, string DisplayName, Func<Rig> Build);

// Single source of truth for which emulated systems are wired up end-to-end,
// their canonical id (CLI arg, --input target, UI System submenu), display
// name, and how to construct one. BbcMicro and AcornSystem1 aren't listed -
// they're not yet complete enough to boot from any of these entry points.
// Apple I *is* listed despite being mid-build (docs/apple-i-plan.md) - its
// Tick() is a no-op until Phase 2, so it'll show a frozen/blank picture, but
// the plan calls for registering it from Phase 0 onward.
//
// Aemula.Console, Aemula.UI and Aemula.Benchmarks each need more than an
// id/factory pair per system (a ROM-picker filter, a benchmark workload) -
// they key their own per-id tables off Id rather than re-declaring the
// id/factory list themselves.
public static class EmulatedSystems
{
    public static readonly IReadOnlyList<SystemDescriptor> All =
    [
        new("appleii", "Apple II+", static () => new Rig(new AppleIISystem())),
        new("applei", "Apple I", BuildAppleI),
        new("atari2600", "Atari 2600", static () => new Rig(new Atari2600System())),
        new("nes", "NES", static () => new Rig(new NesSystem())),
        new("spaceinvaders", "Space Invaders", static () => new Rig(new SpaceInvadersSystem())),
    ];

    // The Apple I as an owner would have equipped it to run BASIC: the ACI
    // cassette card fitted (firmware at $C100, the tape jacks) and the 4K RAM
    // expansion at $E000 that Integer BASIC loads into. A bare board is still
    // constructible programmatically via AppleISystemOptions.Default.
    private static Rig BuildAppleI()
    {
        var system = new AppleISystem(new AppleISystemOptions(cassetteCard: true, ramExpansionAtE000: true));

        // The cassette deck patched to the ACI card by its two audio leads:
        // deck earphone -> ACI comparator, ACI tape-out -> deck mic. The deck
        // resamples between the WAV rate and the rate the card samples the lead
        // at (φ2 = master clock / 14; see AppleISystem.VideoTiming.cs).
        var deck = new CassetteDeck(system.CyclesPerSecond / 14.0);

        var aci = system.CassetteInterface!;
        aci.CassetteInput = deck.ReadPlayback;
        aci.CassetteOutput = deck.WriteCapture;

        return new Rig(system, new IPeripheral[] { deck });
    }

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
