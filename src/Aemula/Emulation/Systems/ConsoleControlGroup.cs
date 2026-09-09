using System.Collections.Generic;

namespace Aemula.Emulation.Systems;

// A labelled run of console controls in the UI status bar. The system's own
// controls come first with a null label (no heading); each connected
// peripheral's controls follow as their own group under the peripheral's name
// ("Cassette"). See Rig.ControlGroups.
public sealed record ConsoleControlGroup(string? Label, IReadOnlyList<ConsoleControl> Controls);
