using System.Collections.Generic;
using Aemula.Debugging;

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
    }
}
