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
public sealed class Rig : IDisposable, IMediaBayHost
{
    // bay id -> the system or peripheral that owns it, so InsertMedia /
    // EjectMedia can forward a call to the right place by id alone.
    private readonly Dictionary<string, IMediaBayHost> _bayOwners = [];

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

        // The machine's own bays first, then each peripheral's, in one flat
        // list. A bay id contributed by more than one host is a wiring bug in
        // the assembler, not something a caller could recover from.
        var bays = new List<MediaBay>();
        RegisterBays(System, bays);
        foreach (var peripheral in Peripherals)
        {
            RegisterBays(peripheral, bays);
        }

        MediaBays = bays;

        // A media change anywhere on the system surfaces off the rig too, so the
        // debugger (wired to the rig) resets its disassembler on a cartridge swap.
        System.MediaChanged += (_, e) => MediaChanged?.Invoke(this, e);
    }

    private void RegisterBays(IMediaBayHost host, List<MediaBay> into)
    {
        foreach (var bay in host.MediaBays)
        {
            if (!_bayOwners.TryAdd(bay.Id, host))
            {
                throw new InvalidOperationException(
                    $"Media bay id '{bay.Id}' is contributed by more than one of the system and its peripherals.");
            }

            into.Add(bay);
        }
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

    // Every media receptacle on this setup - the machine's own plus every
    // cabled peripheral's - as one list, without the caller caring which is which.
    public IReadOnlyList<MediaBay> MediaBays { get; }

    // Re-surfaced from the system (see the constructor).
    public event EventHandler? MediaChanged;

    public void InsertMedia(string bayId, MediaImage image)
    {
        if (!_bayOwners.TryGetValue(bayId, out var owner))
        {
            throw new ArgumentException($"No media bay '{bayId}'.");
        }

        owner.InsertMedia(bayId, image);
    }

    public void EjectMedia(string bayId)
    {
        if (!_bayOwners.TryGetValue(bayId, out var owner))
        {
            throw new ArgumentException($"No media bay '{bayId}'.");
        }

        owner.EjectMedia(bayId);
    }

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
