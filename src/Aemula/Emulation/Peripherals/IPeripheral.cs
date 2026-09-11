using System;
using System.Collections.Generic;
using Aemula.Emulation.Output;
using Aemula.Emulation.Systems;

namespace Aemula.Emulation.Peripherals;

/// <summary>
/// A device that sits next to the computer and is cabled to it - a monitor, a
/// cassette recorder, a printer - rather than a chip on the board. A
/// <see cref="Rig"/> owns a list of these and treats them entirely through this
/// interface; the machine-specific wiring that connects a peripheral's signals
/// to a <see cref="EmulatedSystem"/> is done by whoever assembles the rig, not
/// here.
/// </summary>
public interface IPeripheral : IDisposable
{
    /// <summary>
    /// Short label for the peripheral's console-control group in the UI status
    /// bar (e.g. "Cassette").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// The peripheral's own hand-operated controls (a cassette deck's transport
    /// buttons and counter), surfaced in the status bar under <see cref="Name"/>
    /// and scriptable by mnemonic exactly like a system's own
    /// <see cref="EmulatedSystem.ConsoleControls"/>. Empty if it has none.
    /// </summary>
    IReadOnlyList<ConsoleControl> Controls { get; }

    /// <summary>
    /// Audio this peripheral produces for the host speakers (a cassette deck's
    /// tape monitor), or <see langword="null"/> if it is silent. The
    /// <see cref="Rig"/> mixes this with the system's own audio.
    /// </summary>
    IAudioSource? Audio { get; }

    /// <summary>Return the peripheral to its power-on state.</summary>
    void Reset();

    // A peripheral that accepts removable media (a cassette deck) puts a
    // MediaBay-kind ConsoleControl in Controls; the Rig folds it into its own
    // aggregate bay list the same way it does the system's.
}
