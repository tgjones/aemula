using System;
using System.Collections.Generic;
using Aemula.Emulation.Peripherals;

namespace Aemula.Emulation.Systems;

/// <summary>
/// Binds one <see cref="ExpansionCardOption"/> to the code that actually builds
/// that card. Lives inside the owning system, which is the only place that
/// knows the concrete, pin-level card type - hence the generic
/// <typeparamref name="TCard"/> (the system's own <c>IExpansionCard</c>).
/// </summary>
/// <typeparam name="TCard">The system's pin-level expansion-card interface.</typeparam>
/// <param name="Option">The user-visible choice this binding satisfies.</param>
/// <param name="Install">
/// Builds the card and declares any peripherals it needs. Called once, when the
/// system is constructed with this card selected.
/// </param>
public sealed record ExpansionCardBinding<TCard>(
    ExpansionCardOption Option,
    Func<ExpansionCardInstallation<TCard>> Install);

/// <summary>
/// The result of fitting a card: the pin-level card itself, plus the peripherals
/// it brings with it (an Apple I ACI card brings a cassette deck). The
/// rig assembler builds and attaches each <see cref="Peripherals"/> entry after
/// the system is constructed.
/// </summary>
/// <typeparam name="TCard">The system's pin-level expansion-card interface.</typeparam>
/// <param name="Card">The card to plug into the system's bus.</param>
/// <param name="Peripherals">
/// Cabled devices this card exposes, if any. Each is created and wired by
/// <see cref="RigBuilder"/>.
/// </param>
public sealed record ExpansionCardInstallation<TCard>(
    TCard Card,
    IReadOnlyList<PeripheralRequest> Peripherals);
