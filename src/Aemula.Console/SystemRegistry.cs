using System;
using System.Collections.Generic;
using Aemula.Emulation.Systems.AppleII;
using Aemula.Emulation.Systems.Atari2600;
using Aemula.Emulation.Systems.Nes;
using Aemula.Emulation.Systems.SpaceInvaders;

namespace Aemula.Console;

public static class SystemRegistry
{
    // Frame counting below is defined in terms of EmulatedSystem.Television's
    // CurrentRow, so a system whose Television never locks to a signal cannot
    // be driven by this tool.
    public static readonly Dictionary<string, Func<EmulatedSystem>> Systems = new()
    {
        { "appleii", () => new AppleIISystem() },
        { "atari2600", () => new Atari2600System() },
        { "nes", () => new NesSystem() },
        { "spaceinvaders", () => new SpaceInvadersSystem() },
    };
}
