using System;

namespace Aemula.Emulation.Peripherals;

/// <summary>
/// A declaration that something on the machine - the motherboard itself, or a
/// fitted expansion card - has a device cabled to it. It carries both halves of
/// the wiring that is specific to a given (port, device) pair: how to build the
/// device, and how to patch its signals to the thing that asked for it.
/// The rig assembler runs both once the system exists and adds the device to
/// the rig.
/// </summary>
/// <remarks>
/// "What device" and "how to build it" are the same thing here - the factory -
/// so there is no separate device-kind enum. Build parameters that depend on the
/// system (a cassette deck's sample rate is derived from the CPU clock) are read
/// from the <see cref="PeripheralContext"/>.
/// </remarks>
public sealed class PeripheralRequest
{
    private PeripheralRequest(Func<PeripheralContext, IPeripheral> create, Action<IPeripheral> attach)
    {
        Create = create;
        Attach = attach;
    }

    /// <summary>Builds the device. May read the system clock and the like from the context.</summary>
    public Func<PeripheralContext, IPeripheral> Create { get; }

    /// <summary>Cables the freshly built device to whatever asked for it.</summary>
    public Action<IPeripheral> Attach { get; }

    /// <summary>
    /// Declares a request for a <typeparamref name="T"/>, typed so the caller's
    /// <paramref name="create"/> and <paramref name="attach"/> delegates work
    /// with the concrete device rather than <see cref="IPeripheral"/>.
    /// </summary>
    public static PeripheralRequest For<T>(
        Func<PeripheralContext, T> create,
        Action<T> attach)
        where T : class, IPeripheral
        => new(context => create(context), peripheral => attach((T)peripheral));
}

/// <summary>
/// What a <see cref="PeripheralRequest.Create"/> delegate is given: the
/// just-constructed system, for build parameters that derive from it (clock
/// rate, and the like).
/// </summary>
public sealed record PeripheralContext(EmulatedSystem System);
