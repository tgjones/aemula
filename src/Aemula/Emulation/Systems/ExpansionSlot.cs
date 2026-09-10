using System.Collections.Generic;

namespace Aemula.Emulation.Systems;

/// <summary>
/// One card socket a system exposes for user configuration, and the fixed set
/// of cards that fit it. This is capability data only - immutable, and readable
/// without constructing the system - so the command line and the UI can present
/// the choices and validate a selection generically, with no per-system code.
/// The concrete, pin-level card behind a chosen id is the system's own concern
/// (see the per-system <c>IExpansionCard</c> and its card bindings).
/// </summary>
/// <param name="Id">
/// Stable, lower-case identifier for the socket - "expansion" for the Apple I's
/// single connector, "slot1".."slot7" for the Apple II. Used as the key in
/// <see cref="ExpansionSlotConfiguration"/> and on the command line.
/// </param>
/// <param name="DisplayName">Human-readable name for menus ("Expansion connector", "Slot 3").</param>
/// <param name="Cards">
/// The cards that can be fitted here. The empty socket is always allowed and is
/// not listed among these.
/// </param>
/// <param name="DefaultCardId">
/// The card fitted when the user expresses no preference, or <see langword="null"/>
/// to leave the socket empty by default. Must match one of <see cref="Cards"/>
/// when non-null.
/// </param>
public sealed record ExpansionSlot(
    string Id,
    string DisplayName,
    IReadOnlyList<ExpansionCardOption> Cards,
    string? DefaultCardId = null);

/// <summary>
/// One card a user can pick for an <see cref="ExpansionSlot"/>. Display data
/// only; the binding from <see cref="Id"/> to a constructed card lives in the
/// owning system.
/// </summary>
/// <param name="Id">Stable, lower-case identifier ("aci", "disk-ii").</param>
/// <param name="DisplayName">Human-readable name ("Apple Cassette Interface").</param>
/// <param name="Summary">One-line description for a slots listing or a tooltip, or <see langword="null"/>.</param>
public sealed record ExpansionCardOption(
    string Id,
    string DisplayName,
    string? Summary = null);
