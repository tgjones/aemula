using System;
using System.Collections.Generic;
using System.Linq;
using Aemula.Emulation.Output;
using Aemula.Emulation.Peripherals;
using Aemula.Emulation.Systems;

namespace Aemula;

/// <summary>
/// One whole setup on the desk: the computer (<see cref="System"/>), the monitor
/// it decodes into (<see cref="Television"/>), and whatever external devices are
/// cabled to it (<see cref="Peripherals"/> - a cassette recorder, and the like).
/// </summary>
/// <remarks>
/// <para>
/// The rig owns the pieces and treats the peripherals entirely through
/// <see cref="IPeripheral"/> - it never names a concrete peripheral type. The
/// machine-specific wiring that connects a peripheral's signals to the system
/// (patching a cassette deck's leads to the Apple I ACI card's jacks) is done by
/// whoever assembles the rig - see <c>EmulatedSystems</c> - not here.
/// </para>
/// <para>
/// It is a plain composition, not a base class: the difference between an Apple
/// I rig and an NES rig is entirely in the assembler's wiring, so there is no
/// per-machine subclass.
/// </para>
/// </remarks>
public sealed class Rig : IDisposable
{
    public Rig(EmulatedSystem system, IReadOnlyList<IPeripheral>? peripherals = null)
    {
        System = system;
        Peripherals = peripherals ?? [];

        // The system's own console controls first, unlabelled; then one labelled
        // group per peripheral that has any.
        var groups = new List<ConsoleControlGroup>();
        if (system.ConsoleControls.Count > 0)
        {
            groups.Add(new ConsoleControlGroup(null, system.ConsoleControls));
        }

        foreach (var peripheral in Peripherals)
        {
            if (peripheral.Controls.Count > 0)
            {
                groups.Add(new ConsoleControlGroup(peripheral.Name, peripheral.Controls));
            }
        }

        ControlGroups = groups;

        // Mix the system's audio with every peripheral's. One source (the usual
        // case) is handed straight through; none means silence.
        var sources = new List<IAudioSource>();
        if (system.Audio is { } systemAudio)
        {
            sources.Add(systemAudio);
        }

        foreach (var peripheral in Peripherals)
        {
            if (peripheral.Audio is { } peripheralAudio)
            {
                sources.Add(peripheralAudio);
            }
        }

        Audio = sources.Count switch
        {
            0 => NullAudioSource.Instance,
            1 => sources[0],
            _ => new MixedAudioSource(sources),
        };
    }

    public EmulatedSystem System { get; }

    // The system decodes its composite video into this. Owned by the system for
    // now (every system pushes samples straight into it); the rig just surfaces
    // it so tooling doesn't reach through System.
    public Television Television => System.Television;

    public IReadOnlyList<IPeripheral> Peripherals { get; }

    public IReadOnlyList<ConsoleControlGroup> ControlGroups { get; }

    // Every console control across the system and all peripherals, flattened -
    // for the headless --input runner, which resolves controls by mnemonic and
    // doesn't care which group they came from.
    public IEnumerable<ConsoleControl> AllControls => ControlGroups.SelectMany(group => group.Controls);

    public IAudioSource Audio { get; }

    public T? GetPeripheral<T>() where T : class, IPeripheral
    {
        foreach (var peripheral in Peripherals)
        {
            if (peripheral is T match)
            {
                return match;
            }
        }

        return null;
    }

    // A picked file is offered to each peripheral first (a cassette deck takes a
    // .wav as a tape), then falls back to the system's own loader.
    public void LoadProgram(string filePath)
    {
        if (!string.IsNullOrEmpty(filePath))
        {
            foreach (var peripheral in Peripherals)
            {
                if (peripheral.TryLoadMedia(filePath))
                {
                    return;
                }
            }
        }

        System.LoadProgram(filePath);
    }

    // A machine reset pulses the system only. Peripherals keep their state - a
    // cassette left running keeps turning, exactly as a real deck would.
    public void Reset() => System.Reset();

    public void RunForDuration(TimeSpan duration) => System.RunForDuration(duration);

    public void Dispose()
    {
        foreach (var peripheral in Peripherals)
        {
            peripheral.Dispose();
        }

        System.Dispose();
    }
}
