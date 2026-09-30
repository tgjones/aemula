using System.Collections.Generic;
using Aemula.Debugging;
using Aemula.Debugging.LogicAnalyzer;
using Aemula.UI.LogicAnalyzer;

namespace Aemula.UI.Debugging;

// The ImGui-rendering half of a Debugger subclass: which windows a running
// system's debugger view is built from. Debugger itself (breakpoints,
// disassembly, stepping) stays in Aemula and knows nothing about ImGui -
// this hierarchy, and the window classes it constructs, lives here instead,
// since Aemula can't reference back into Aemula.UI to build them itself. See
// DebuggerUIFactory for how a live Debugger instance finds its DebuggerUI.
public abstract class DebuggerUI(Debugger debugger)
{
    protected Debugger Debugger => debugger;

    public virtual void CreateDebuggerWindows(List<DebuggerWindow> result)
    {
        result.Add(new DisassemblyWindow(debugger));

        if (debugger.CreateChannelNodes().Count > 0)
        {
            result.Add(new LogicAnalyzerWindow(debugger, CreateSampleClock()));
        }
    }

    // Null samples once per Debugger.Ticked. A system whose signals change
    // faster than that (Atari2600's oversampled composite video) overrides
    // this to supply its own finer-grained clock.
    protected virtual SampleClock? CreateSampleClock() => null;
}
