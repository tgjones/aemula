using System;
using System.Collections.Generic;
using Aemula.Emulation.Systems.AppleII;
using Aemula.Emulation.Systems.Atari2600;
using Aemula.Emulation.Systems.Nes;
using Aemula.Emulation.Systems.SpaceInvaders;

namespace Aemula.Emulation.Systems;

public sealed record SystemDescriptor(string Id, string DisplayName, Func<EmulatedSystem> Create);

// Single source of truth for which emulated systems are wired up end-to-end,
// their canonical id (CLI arg, --input target, UI System submenu), display
// name, and how to construct one. BbcMicro and AcornSystem1 aren't listed -
// they're not yet complete enough to boot from any of these entry points.
//
// Aemula.Console, Aemula.UI and Aemula.Benchmarks each need more than an
// id/factory pair per system (a ROM-picker filter, a benchmark workload) -
// they key their own per-id tables off Id rather than re-declaring the
// id/factory list themselves.
public static class EmulatedSystems
{
    public static readonly IReadOnlyList<SystemDescriptor> All =
    [
        new("appleii", "Apple II+", static () => new AppleIISystem()),
        new("atari2600", "Atari 2600", static () => new Atari2600System()),
        new("nes", "NES", static () => new NesSystem()),
        new("spaceinvaders", "Space Invaders", static () => new SpaceInvadersSystem()),
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
