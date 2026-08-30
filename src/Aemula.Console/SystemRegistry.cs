using System;
using System.Collections.Generic;
using Aemula.Emulation.Systems.AppleII;
using Aemula.Emulation.Systems.Atari2600;
using Aemula.Emulation.Systems.SpaceInvaders;

namespace Aemula.Console;

public static class SystemRegistry
{
    // Only the 3 systems that implement IHasTelevision - frame counting below is
    // defined in terms of Television.CurrentRow, which NesSystem have no
    // equivalent of, so they're out of scope for this tool rather than half-supported.
    public static readonly Dictionary<string, Func<EmulatedSystem>> Systems = new()
    {
        { "appleii", () => new AppleIISystem() },
        { "atari2600", () => new Atari2600System() },
        { "spaceinvaders", () => new SpaceInvadersSystem() },
    };
}
