using System;
using System.Collections.Generic;
using Aemula.Emulation.Systems.AppleII;
using Aemula.Emulation.Systems.Atari2600;
using Aemula.Emulation.Systems.Nes;
using Aemula.Emulation.Systems.SpaceInvaders;

namespace Aemula.Console;

public static class SystemRegistry
{
    // Every system here implements IHasTelevision - frame counting below is
    // defined in terms of Television.CurrentRow, so a system without a live
    // Television cannot be driven by this tool.
    public static readonly Dictionary<string, Func<EmulatedSystem>> Systems = new()
    {
        { "appleii", () => new AppleIISystem() },
        { "atari2600", () => new Atari2600System() },
        { "nes", () => new NesSystem() },
        { "spaceinvaders", () => new SpaceInvadersSystem() },
    };
}
