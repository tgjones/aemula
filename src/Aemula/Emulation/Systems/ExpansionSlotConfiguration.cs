using System;
using System.Collections.Generic;
using System.Linq;

namespace Aemula.Emulation.Systems;

/// <summary>
/// A user's choice of which card sits in each of a system's
/// <see cref="ExpansionSlot"/>s. Built from (slot id, card id) pairs - the
/// command line accumulates them from repeated <c>--slot</c> arguments, the UI
/// menu emits one per pick - and read back through the <see cref="this"/>
/// indexer. A <see langword="null"/> card id means "force this slot empty",
/// overriding the slot's default.
/// </summary>
public sealed class ExpansionSlotConfiguration
{
    // No choices at all: every slot falls back to its DefaultCardId. This is
    // what a system built with no slot preferences expressed gets.
    public static ExpansionSlotConfiguration Empty { get; } = new(Array.Empty<(string, string?)>());

    // slot id -> chosen card id (null = deliberately empty). A slot absent from
    // here has no expressed preference and takes its default at resolve time.
    private readonly Dictionary<string, string?> _choices;

    /// <param name="choices">
    /// (slot id, card id) pairs. A null card id forces the slot empty. Order is
    /// not significant; a repeated slot id is last-wins.
    /// </param>
    public ExpansionSlotConfiguration(IEnumerable<(string SlotId, string? CardId)> choices)
    {
        _choices = new Dictionary<string, string?>();
        foreach (var (slotId, cardId) in choices)
        {
            _choices[slotId] = cardId;
        }
    }

    /// <summary>
    /// The card id chosen for <paramref name="slotId"/>, or <see langword="null"/>
    /// if the slot was left empty or never mentioned. Prefer
    /// <see cref="ResolvedAgainst"/> when defaults matter.
    /// </summary>
    public string? this[string slotId] => _choices.GetValueOrDefault(slotId);

    /// <summary>Whether a preference (a card or a deliberate empty) was expressed for <paramref name="slotId"/>.</summary>
    public bool HasChoiceFor(string slotId) => _choices.ContainsKey(slotId);

    /// <summary>
    /// A copy of this configuration with <paramref name="slotId"/> set to
    /// <paramref name="cardId"/> (<see langword="null"/> = empty). Used by the UI
    /// to fold in one menu pick.
    /// </summary>
    public ExpansionSlotConfiguration With(string slotId, string? cardId)
    {
        var next = new Dictionary<string, string?>(_choices) { [slotId] = cardId };
        return new ExpansionSlotConfiguration(next.Select(kvp => (kvp.Key, kvp.Value)));
    }

    /// <summary>
    /// Resolves this configuration against a system's actual slot list: fills in
    /// each unspecified slot with its <see cref="ExpansionSlot.DefaultCardId"/>,
    /// and throws if a choice names a slot the system doesn't have or a card the
    /// slot doesn't offer.
    /// </summary>
    public ExpansionSlotConfiguration ResolvedAgainst(IReadOnlyList<ExpansionSlot> slots)
    {
        foreach (var (slotId, cardId) in _choices)
        {
            var slot = slots.FirstOrDefault(s => s.Id == slotId)
                ?? throw new ArgumentException(
                    $"No expansion slot '{slotId}'. Slots: {(slots.Count == 0 ? "(none)" : string.Join(", ", slots.Select(s => s.Id)))}.");

            if (cardId != null && slot.Cards.All(c => c.Id != cardId))
            {
                throw new ArgumentException(
                    $"Slot '{slotId}' has no card '{cardId}'. Cards: none, {string.Join(", ", slot.Cards.Select(c => c.Id))}.");
            }
        }

        var resolved = slots.Select(slot =>
            (slot.Id, _choices.TryGetValue(slot.Id, out var chosen) ? chosen : slot.DefaultCardId));

        return new ExpansionSlotConfiguration(resolved);
    }
}
