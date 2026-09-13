using System;
using Aemula.Debugging;
using Aemula.Emulation.Systems.AppleI.Debugging;
using Aemula.Emulation.Systems.AppleII.Debugging;
using Aemula.Emulation.Systems.Atari2600.Debugging;
using Aemula.Emulation.Systems.Nes.Debugging;
using Aemula.Emulation.Systems.SpaceInvaders.Debugging;
using Aemula.Emulation.Systems.ZX80.Debugging;

namespace Aemula.UI.Debugging;

// DebuggerHost only ever holds a Debugger (EmulatedSystem.CreateDebugger's
// return type), so this is the one place that has to recover the concrete
// type to find the matching DebuggerUI - everything each DebuggerUI does
// once constructed is static, known composition, not runtime dispatch.
public static class DebuggerUIFactory
{
    public static DebuggerUI Create(Debugger debugger) => debugger switch
    {
        AppleIDebugger d => new AppleIDebuggerUI(d),
        AppleIIDebugger d => new AppleIIDebuggerUI(d),
        Atari2600Debugger d => new Atari2600DebuggerUI(d),
        NesDebugger d => new NesDebuggerUI(d),
        SpaceInvadersDebugger d => new SpaceInvadersDebuggerUI(d),
        ZX80Debugger d => new ZX80DebuggerUI(d),
        _ => throw new NotSupportedException($"No DebuggerUI registered for {debugger.GetType()}."),
    };
}
