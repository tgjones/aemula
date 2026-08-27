using System.Collections.Generic;
using System.Threading.Tasks;
using Aemula.Emulation.Chips.Tia;

namespace Aemula.Tests.Emulation.Chips.Tia;

// TIA audio tests. Two layers:
//
//  * TiaAudioChannel on its own - construct a channel, set AUDC/AUDF/AUDV,
//    call Tick() in a loop and watch Output / Sample. One Tick() is one audio
//    clock (phase0 + phase1); with AUDF = 0 every Tick() is a divider
//    underflow, so "period N divider-underflows" is just N ticks.
//  * A bare TiaChip (CS1-selected, Osc-stepped, same harness shape as
//    TiaBallTests) to confirm the register writes land and the two channels
//    reach the AUD0 / AUD1 pins independently, twice per scanline.
//
// The expected waveforms are cross-checked against Ron Fries' TIASound.c AUDC
// table: 0 and 11 silent, 4/5 a pure divide-by-two tone, poly periods
// 15 / 31 / 511.
public class TiaAudioTests
{
    private const byte Audc0 = 0x15;
    private const byte Audc1 = 0x16;
    private const byte Audf0 = 0x17;
    private const byte Audf1 = 0x18;
    private const byte Audv0 = 0x19;
    private const byte Audv1 = 0x1A;

    // --- TiaAudioChannel unit level ---------------------------------------

    private static bool[] RunChannel(byte audc, byte audf, int warmup, int count)
    {
        var channel = new TiaAudioChannel { Audc = audc, Audf = audf, Audv = 15 };

        for (var i = 0; i < warmup; i++)
        {
            channel.Tick();
        }

        var output = new bool[count];
        for (var i = 0; i < count; i++)
        {
            channel.Tick();
            output[i] = channel.Output;
        }

        return output;
    }

    // True if seq[i] == seq[i + period] everywhere it can be checked.
    private static bool RepeatsWith(bool[] seq, int period)
    {
        for (var i = 0; i + period < seq.Length; i++)
        {
            if (seq[i] != seq[i + period])
            {
                return false;
            }
        }

        return true;
    }

    // Indices where the output flips value.
    private static List<int> Edges(bool[] seq)
    {
        var edges = new List<int>();
        for (var i = 1; i < seq.Length; i++)
        {
            if (seq[i] != seq[i - 1])
            {
                edges.Add(i);
            }
        }

        return edges;
    }

    [Test]
    [Arguments((byte)0)]
    [Arguments((byte)11)]
    public async Task SilentAudcModesHoldTheOutputConstant(byte audc)
    {
        // AUDC 0 and 11 are "set to 1" on real hardware: after the counters
        // settle the output never moves again.
        var seq = RunChannel(audc, audf: 0, warmup: 200, count: 600);

        foreach (var bit in seq)
        {
            await Assert.That(bit).IsEqualTo(seq[0]);
        }
    }

    [Test]
    [Arguments((byte)1)]
    [Arguments((byte)3)]
    [Arguments((byte)7)]
    public async Task PureToneTogglesEveryAudfPlusOneTicks(byte audf)
    {
        // AUDC 4 is a pure divide-by-two: the output flips once per divider
        // underflow, i.e. every (AUDF + 1) ticks, for a full period of
        // 2 * (AUDF + 1).
        var period = 2 * (audf + 1);
        var seq = RunChannel(audc: 4, audf, warmup: 20, count: period * 8);

        var edges = Edges(seq);
        await Assert.That(edges.Count).IsGreaterThan(4);
        for (var i = 1; i < edges.Count; i++)
        {
            await Assert.That(edges[i] - edges[i - 1]).IsEqualTo(audf + 1);
        }

        await Assert.That(RepeatsWith(seq, period)).IsTrue();
    }

    [Test]
    public async Task FourBitPolyRepeatsEveryFifteenUnderflows()
    {
        var seq = RunChannel(audc: 1, audf: 0, warmup: 300, count: 15 * 8);

        await Assert.That(RepeatsWith(seq, 15)).IsTrue();

        // 15 is the minimal period - nothing shorter repeats (this also
        // proves the output actually oscillates).
        for (var p = 1; p < 15; p++)
        {
            await Assert.That(RepeatsWith(seq, p)).IsFalse();
        }
    }

    [Test]
    public async Task FourBitPolyPeriodScalesWithAudf()
    {
        // "15 divider-underflows", not "15 ticks": with AUDF = 1 the divider
        // underflows every other tick, so the tick period doubles to 30.
        var seq = RunChannel(audc: 1, audf: 1, warmup: 300, count: 30 * 6);

        await Assert.That(RepeatsWith(seq, 30)).IsTrue();
        await Assert.That(RepeatsWith(seq, 15)).IsFalse();
    }

    [Test]
    public async Task FiveBitPolyRepeatsEveryThirtyOneUnderflows()
    {
        var seq = RunChannel(audc: 9, audf: 0, warmup: 300, count: 31 * 6);

        await Assert.That(RepeatsWith(seq, 31)).IsTrue();

        for (var p = 1; p < 31; p++)
        {
            await Assert.That(RepeatsWith(seq, p)).IsFalse();
        }
    }

    [Test]
    public async Task NineBitWhiteNoiseRepeatsEveryFiveHundredElevenUnderflows()
    {
        var seq = RunChannel(audc: 8, audf: 0, warmup: 600, count: 511 * 4);

        await Assert.That(RepeatsWith(seq, 511)).IsTrue();

        // Spot-check a few shorter periods that a lesser poly would show.
        foreach (var p in new[] { 15, 31, 255, 510 })
        {
            await Assert.That(RepeatsWith(seq, p)).IsFalse();
        }
    }

    [Test]
    public async Task VolumeIsStoredAndScalesTheNumericSample()
    {
        var channel = new TiaAudioChannel { Audc = 4, Audf = 0, Audv = 13 };
        await Assert.That(channel.Audv).IsEqualTo((byte)13);

        // AUDC 4 toggles the waveform bit every tick at AUDF 0; check both
        // half-cycles.
        var sawHigh = false;
        var sawLow = false;
        for (var i = 0; i < 8; i++)
        {
            channel.Tick();
            if (channel.Output)
            {
                await Assert.That(channel.Sample).IsEqualTo((byte)13);
                sawHigh = true;
            }
            else
            {
                await Assert.That(channel.Sample).IsEqualTo((byte)0);
                sawLow = true;
            }
        }

        await Assert.That(sawHigh).IsTrue();
        await Assert.That(sawLow).IsTrue();
    }

    [Test]
    public async Task ChannelsAreIndependent()
    {
        // Same seed, different control: the two channels do not share state.
        var toneA = new TiaAudioChannel { Audc = 4, Audf = 1, Audv = 7 };
        var toneB = new TiaAudioChannel { Audc = 4, Audf = 4, Audv = 4 };

        var flipsA = 0;
        var flipsB = 0;
        var prevA = toneA.Output;
        var prevB = toneB.Output;
        for (var i = 0; i < 200; i++)
        {
            toneA.Tick();
            toneB.Tick();
            if (toneA.Output != prevA)
            {
                flipsA++;
                prevA = toneA.Output;
            }

            if (toneB.Output != prevB)
            {
                flipsB++;
                prevB = toneB.Output;
            }
        }

        // AUDF 1 flips roughly 2.5x as often as AUDF 4 ((4+1)/(1+1)).
        await Assert.That(flipsA).IsGreaterThan(flipsB);
    }

    // --- Bare TiaChip integration ---------------------------------------

    private static TiaChip NewTia() => new() { CS1 = true };

    private static void Write(TiaChip tia, byte address, byte data)
    {
        tia.RW = false;
        tia.Address = address;
        tia.Data05 = (byte)(data & 0x3F);
        tia.Data67 = (byte)(data >> 6);
        tia.Phi2 = false;
        tia.Phi2 = true;
    }

    private static void Tick(TiaChip tia)
    {
        tia.Osc = false;
        tia.Osc = true;
    }

    [Test]
    public async Task RegisterWritesLandOnTheAddressedChannelOnly()
    {
        var tia = NewTia();

        Write(tia, Audc0, 0x0B);
        Write(tia, Audf0, 0x1F);
        Write(tia, Audv0, 0x0F);

        await Assert.That(tia._audio0.Audc).IsEqualTo((byte)0x0B);
        await Assert.That(tia._audio0.Audf).IsEqualTo((byte)0x1F);
        await Assert.That(tia._audio0.Audv).IsEqualTo((byte)0x0F);

        // Channel 1 untouched.
        await Assert.That(tia._audio1.Audc).IsEqualTo((byte)0);
        await Assert.That(tia._audio1.Audf).IsEqualTo((byte)0);
        await Assert.That(tia._audio1.Audv).IsEqualTo((byte)0);

        Write(tia, Audc1, 0x07);
        Write(tia, Audf1, 0x05);
        Write(tia, Audv1, 0x0A);

        await Assert.That(tia._audio1.Audc).IsEqualTo((byte)0x07);
        await Assert.That(tia._audio1.Audf).IsEqualTo((byte)0x05);
        await Assert.That(tia._audio1.Audv).IsEqualTo((byte)0x0A);

        // Channel 0 unchanged by the channel-1 writes.
        await Assert.That(tia._audio0.Audc).IsEqualTo((byte)0x0B);
        await Assert.That(tia._audio0.Audf).IsEqualTo((byte)0x1F);
        await Assert.That(tia._audio0.Audv).IsEqualTo((byte)0x0F);
    }

    [Test]
    public async Task Audf05BitMaskIsApplied()
    {
        var tia = NewTia();

        // Only D0-D4 of AUDF and D0-D3 of AUDC/AUDV are wired.
        Write(tia, Audf0, 0xFF);
        Write(tia, Audc0, 0xFF);
        Write(tia, Audv0, 0xFF);

        await Assert.That(tia._audio0.Audf).IsEqualTo((byte)0x1F);
        await Assert.That(tia._audio0.Audc).IsEqualTo((byte)0x0F);
        await Assert.That(tia._audio0.Audv).IsEqualTo((byte)0x0F);
    }

    [Test]
    public async Task Audc4MakesAud0ToggleWhileAud1HoldsStill()
    {
        var tia = NewTia();

        // Channel 0: a pure tone. Channel 1: left at AUDC 0 (silent).
        Write(tia, Audc0, 0x04);
        Write(tia, Audf0, 0x05);

        // Warm up enough lines for channel 1's power-on transient to settle
        // (AUDC 0's pulse counter needs ~20 audio ticks == ~10 lines).
        for (var i = 0; i < 20 * 228; i++)
        {
            Tick(tia);
        }

        var aud0Values = new HashSet<bool>();
        var aud1Values = new HashSet<bool>();
        for (var i = 0; i < 40 * 228; i++)
        {
            Tick(tia);
            aud0Values.Add(tia.Aud0);
            aud1Values.Add(tia.Aud1);
        }

        // AUD0 visits both levels; AUD1 never moves.
        await Assert.That(aud0Values.Count).IsEqualTo(2);
        await Assert.That(aud1Values.Count).IsEqualTo(1);
    }

    [Test]
    public async Task AudioIsClockedTwicePerScanline()
    {
        var tia = NewTia();

        // AUDC 4 at AUDF 0 flips the output on every audio tick, so counting
        // AUD0 edges over a known number of scanlines measures the tick rate.
        Write(tia, Audc0, 0x04);
        Write(tia, Audf0, 0x00);

        // Settle onto a line boundary.
        while (!tia.Blk)
        {
            Tick(tia);
        }

        var edges = 0;
        var prev = tia.Aud0;
        const int lines = 100;
        for (var i = 0; i < lines * 228; i++)
        {
            Tick(tia);
            if (tia.Aud0 != prev)
            {
                edges++;
                prev = tia.Aud0;
            }
        }

        // Two ticks per line, one flip per tick.
        await Assert.That(edges).IsEqualTo(lines * 2);
    }
}
