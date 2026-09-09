using System.Collections.Generic;
using System.Threading.Tasks;
using Aemula.Emulation.Peripherals.Cassette;
using Aemula.Emulation.Systems.AppleI;
using Aemula.Emulation.Systems.AppleI.Cassette;
using Aemula.Emulation.Systems.AppleI.Roms;

namespace Aemula.Tests.Emulation.Systems.AppleI;

public class AppleCassetteInterfaceCardTests
{
    private const double Phi2Rate = 14_318_180.0 / 14.0;

    // The card on its own - both audio leads unplugged. Enough for the address
    // decode / firmware-read tests.
    private static AppleCassetteInterfaceCard NewCard() => new();

    // The card with a cassette deck patched to its two jacks, the way the rig
    // assembler wires them.
    private static (AppleCassetteInterfaceCard Card, CassetteDeck Deck) NewCardWithDeck()
    {
        var card = new AppleCassetteInterfaceCard();
        var deck = new CassetteDeck(Phi2Rate);
        card.CassetteInput = deck.ReadPlayback;
        card.CassetteOutput = deck.WriteCapture;
        return (card, deck);
    }

    private static byte BusRead(IExpansionCard card, ushort address)
    {
        card.Address = address;
        card.RW = true;
        card.Phi2 = false;
        card.Phi2 = true;
        card.Phi2 = false;
        return card.DrivesData ? card.DataBus : (byte)0xFF;
    }

    private static void BusAccess(IExpansionCard card, ushort address, bool read)
    {
        card.Address = address;
        card.RW = read;
        card.Phi2 = false;
        card.Phi2 = true;
        card.Phi2 = false;
    }

    [Test]
    public async Task FirmwareReadsBackAtC100()
    {
        var card = NewCard();

        await Assert.That(BusRead(card, 0xC100)).IsEqualTo(AciRom.Image[0]);
        await Assert.That(BusRead(card, 0xC1FF)).IsEqualTo(AciRom.Image[0xFF]);
        await Assert.That(BusRead(card, 0xC17A)).IsEqualTo(AciRom.Image[0x7A]);
    }

    [Test]
    public async Task DoesNotDriveTheBusOutsideItsRange()
    {
        var card = NewCard();

        BusRead(card, 0x0200);
        await Assert.That(card.DrivesData).IsEqualTo(false);

        BusRead(card, 0xC200);
        await Assert.That(card.DrivesData).IsEqualTo(false);
    }

    [Test]
    public async Task EveryIoAccessTogglesTheTapeOutFlipFlop()
    {
        var (card, deck) = NewCardWithDeck();
        deck.StartRecording();

        // Access $C000 once every 60 φ2 cycles - a ~8.5 kHz square wave on the
        // tape-out line - for 200 toggles; other cycles touch RAM, which the
        // card ignores.
        const int half = 60;
        const int toggles = 200;
        for (var cycle = 0; cycle < half * toggles; cycle++)
        {
            BusAccess(card, cycle % half == 0 ? (ushort)0xC000 : (ushort)0x0000, read: true);
        }

        deck.StopRecording();
        var samples = deck.RecordedSamples;

        var edges = 0;
        for (var i = 1; i < samples.Count; i++)
        {
            if ((samples[i] > 0f) != (samples[i - 1] > 0f))
            {
                edges++;
            }
        }

        // 12 000 φ2 cycles at ~21.3 cycles per 48 kHz output sample.
        await Assert.That(samples.Count).IsBetween(540, 580);

        // One recorded edge per tape-out toggle (the sampler is far faster
        // than the toggle rate, so none are missed and none double-count).
        await Assert.That(edges).IsBetween(190, 200);
    }

    [Test]
    public async Task TapeInputComparatorFlipsTheByteReadFromC081()
    {
        var (card, deck) = NewCardWithDeck();

        // A slow square wave: +/-0.8 for 400 φ2 cycles each half.
        const int half = 400;
        var tape = new float[half * 8];
        for (var i = 0; i < tape.Length; i++)
        {
            tape[i] = (i / half) % 2 == 0 ? 0.8f : -0.8f;
        }

        deck.InsertTape(tape, (int)Phi2Rate);
        deck.Play();

        var lowByte = AciRom.Image[0x80];
        var highByte = AciRom.Image[0x81];
        await Assert.That(lowByte).IsNotEqualTo(highByte);

        var seenLow = false;
        var seenHigh = false;
        var transitions = 0;
        var last = 0xFF;

        for (var cycle = 0; cycle < tape.Length; cycle++)
        {
            var value = BusRead(card, 0xC081);
            if (value == lowByte)
            {
                seenLow = true;
            }
            else if (value == highByte)
            {
                seenHigh = true;
            }

            if (value != last && last != 0xFF)
            {
                transitions++;
            }

            last = value;
        }

        await Assert.That(seenLow).IsEqualTo(true);
        await Assert.That(seenHigh).IsEqualTo(true);

        // 8 half-cycles in the tape -> the squared-up output changes ~7 times
        // (allowing a cycle or two of comparator sync latency at each edge).
        await Assert.That(transitions).IsBetween(6, 10);
    }

    [Test]
    public async Task RomReadIsUnaffectedByTapeInput()
    {
        var (card, deck) = NewCardWithDeck();

        var tape = new float[2000];
        for (var i = 0; i < tape.Length; i++)
        {
            tape[i] = (i / 50) % 2 == 0 ? 0.9f : -0.9f;
        }

        deck.InsertTape(tape, (int)Phi2Rate);
        deck.Play();

        var reads = new HashSet<byte>();
        for (var i = 0; i < tape.Length; i++)
        {
            reads.Add(BusRead(card, 0xC17A));
        }

        // $C17A is in ROM space ($C100-$C1FF): A0 is never substituted there,
        // so the byte is constant regardless of the tape.
        await Assert.That(reads.Count).IsEqualTo(1);
    }
}
