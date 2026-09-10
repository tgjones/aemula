using System.Collections.Generic;
using System.Linq;
using Aemula.Emulation.Peripherals;
using Aemula.Emulation.Peripherals.Cassette;
using Aemula.Emulation.Systems.AppleI.Cassette;

namespace Aemula.Emulation.Systems.AppleI;

// The Apple I's expansion configuration: one 44-pin connector, and the cards
// that fit it. The binding list is the only place a concrete card or peripheral
// type is named; Catalog is a plain-data projection for the command line and
// the UI to present and validate a choice without touching this assembly's card
// classes.
internal static class AppleIExpansionSlots
{
    public const string ExpansionSlotId = "expansion";
    public const string CassetteInterfaceCardId = "aci";

    public static readonly IReadOnlyList<ExpansionCardBinding<IExpansionCard>> ExpansionConnectorCards =
    [
        new ExpansionCardBinding<IExpansionCard>(
            new ExpansionCardOption(
                CassetteInterfaceCardId,
                "Apple Cassette Interface",
                "Woz's cassette card: firmware at $C100 and tape in/out jacks for loading and saving programs."),
            Install: () =>
            {
                var card = new AppleCassetteInterfaceCard();
                return new ExpansionCardInstallation<IExpansionCard>(card,
                [
                    // Deck earphone -> ACI comparator, ACI tape-out -> deck mic.
                    // The deck resamples between the WAV rate and the rate the
                    // card samples the lead at (phi2 = master clock / 14).
                    PeripheralRequest.For<CassetteDeck>(
                        context => new CassetteDeck(context.System.CyclesPerSecond / 14.0),
                        deck =>
                        {
                            card.CassetteInput = deck.ReadPlayback;
                            card.CassetteOutput = deck.WriteCapture;
                        }),
                ]);
            }),
    ];

    public static readonly IReadOnlyList<ExpansionSlot> Catalog =
    [
        new ExpansionSlot(
            ExpansionSlotId,
            "Expansion connector",
            [.. ExpansionConnectorCards.Select(card => card.Option)],
            DefaultCardId: CassetteInterfaceCardId),
    ];

    // The binding for a resolved card id, or null for an empty connector.
    public static ExpansionCardBinding<IExpansionCard>? FindBinding(string? cardId) =>
        cardId is null ? null : ExpansionConnectorCards.FirstOrDefault(card => card.Option.Id == cardId);
}
