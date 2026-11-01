using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Aemula.Emulation.Chips.SerialUla;

namespace Aemula.Tests.Emulation.Chips.SerialUla;

public class SerialUlaChipTests
{
    private static SerialUlaChip Create(byte control)
    {
        var ula = new SerialUlaChip();
        Write(ula, control);
        return ula;
    }

    private static void Write(SerialUlaChip ula, byte value)
    {
        ula.Data = value;
        ula.CS = false;
        ula.E = true;
        ula.E = false;
        ula.CS = true;
    }

    private static void Clock(SerialUlaChip ula)
    {
        ula.Clk = true;
        ula.Clk = false;
    }

    // Clocks the ULA for the given clocks, feeding it a tape tone whose
    // rising edges are the given number of clocks apart, and returns how many
    // clocks each of RxD's levels lasted.
    private static void FeedTone(SerialUlaChip ula, int period, int cycles)
    {
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            for (var clock = 0; clock < period; clock++)
            {
                ula.CasIn = clock < period / 2;
                Clock(ula);
            }
        }
    }

    private static int CountEdges(SerialUlaChip ula, bool rising, int clocks)
    {
        var edges = 0;
        Clock(ula);
        var previous = ula.Rxc;

        for (var clock = 0; clock < clocks; clock++)
        {
            Clock(ula);

            if (ula.Rxc != previous && ula.Rxc == rising)
            {
                edges++;
            }

            previous = ula.Rxc;
        }

        return edges;
    }

    [Test]
    [Arguments((byte)0x48, 1024 / 16)]
    [Arguments((byte)0x68, 1024 / 64)]
    [Arguments((byte)0x50, 1024 / 4)]
    [Arguments((byte)0x70, 1024 / 8)]
    [Arguments((byte)0x78, 1024 / 256)]
    [Arguments((byte)0x58, 1024 / 128)]
    public async Task In_RS423_mode_the_receive_clock_is_the_input_clock_divided_to_64_times_the_baud_rate(byte control, int risingEdges)
    {
        var ula = Create(control);

        await Assert.That(CountEdges(ula, rising: true, 1024)).IsEqualTo(risingEdges);
    }

    [Test]
    public async Task The_transmit_clock_rate_is_in_bits_0_to_2_and_the_cassette_receives_at_the_same_rate()
    {
        var ula = Create(0b00_000_101);

        var transmitEdges = 0;
        Clock(ula);
        var previous = ula.Txc;

        for (var clock = 0; clock < 1024; clock++)
        {
            Clock(ula);

            if (ula.Txc && !previous)
            {
                transmitEdges++;
            }

            previous = ula.Txc;
        }

        await Assert.That(transmitEdges).IsEqualTo(1024 / 64);
        await Assert.That(CountEdges(ula, rising: true, 1024)).IsEqualTo(1024 / 64);
    }

    [Test]
    public async Task The_motor_relay_follows_bit_7()
    {
        var ula = Create(0x80);
        await Assert.That(ula.Motor).IsTrue();

        Write(ula, 0x00);
        await Assert.That(ula.Motor).IsFalse();
    }

    [Test]
    public async Task A_write_is_latched_only_while_selected_as_the_bus_clock_falls()
    {
        var ula = new SerialUlaChip();

        ula.Data = 0x55;
        ula.CS = true;
        ula.E = true;
        ula.E = false;
        await Assert.That(ula.Control).IsEqualTo((byte)0x00);

        ula.CS = false;
        ula.E = true;
        await Assert.That(ula.Control).IsEqualTo((byte)0x00);

        ula.E = false;
        await Assert.That(ula.Control).IsEqualTo((byte)0x55);
    }

    [Test]
    public async Task Idle_it_sends_the_2400Hz_leader_tone()
    {
        var ula = Create(0x00);

        var risingEdges = 0;
        Clock(ula);
        var previous = ula.CasOut;

        for (var clock = 0; clock < 4096; clock++)
        {
            Clock(ula);

            if (ula.CasOut && !previous)
            {
                risingEdges++;
            }

            previous = ula.CasOut;
        }

        // 4096 clocks of 2404Hz tone are eight cycles.
        await Assert.That(risingEdges).IsEqualTo(8);
    }

    [Test]
    public async Task A_zero_bit_is_sent_as_one_1200Hz_cycle()
    {
        var ula = Create(0x00);
        ula.TxD = false;

        var risingEdges = 0;
        var previous = false;

        // The tone is picked at the start of each 1024-clock window.
        for (var clock = 0; clock < 1024 * 4 + 1024; clock++)
        {
            Clock(ula);

            if (clock >= 1024 && ula.CasOut && !previous)
            {
                risingEdges++;
            }

            previous = ula.CasOut;
        }

        await Assert.That(risingEdges).IsEqualTo(4);
    }

    [Test]
    public async Task Tone_changes_only_where_a_cycle_begins()
    {
        var ula = Create(0x00);

        var clocksHigh = new List<int>();
        var runLength = 0;
        var previous = false;

        for (var clock = 0; clock < 1024 * 8; clock++)
        {
            ula.TxD = (clock / 1500 % 2) == 0;
            Clock(ula);

            if (ula.CasOut != previous)
            {
                if (previous)
                {
                    clocksHigh.Add(runLength);
                }

                runLength = 0;
            }

            runLength++;
            previous = ula.CasOut;
        }

        // Every high half-cycle is a whole half-cycle of one tone or the other
        // (the first run is cut short by where the count began).
        await Assert.That(clocksHigh.Count).IsGreaterThan(2);

        foreach (var length in clocksHigh.Skip(1))
        {
            await Assert.That(length == 256 || length == 512).IsTrue();
        }
    }

    [Test]
    public async Task A_run_of_2400Hz_cycles_holds_RxD_high_and_asserts_carrier_detect_after_a_while()
    {
        var ula = Create(0x00);

        FeedTone(ula, 512, 20);
        await Assert.That(ula.Rxd).IsTrue();
        await Assert.That(ula.DcdBar).IsTrue();

        FeedTone(ula, 512, 400);
        await Assert.That(ula.Rxd).IsTrue();
        await Assert.That(ula.DcdBar).IsFalse();
    }

    [Test]
    public async Task A_1200Hz_cycle_drops_RxD_but_carrier_detect_holds_through_data()
    {
        var ula = Create(0x00);
        FeedTone(ula, 512, 400);

        FeedTone(ula, 1024, 1);
        FeedTone(ula, 1024, 1);

        await Assert.That(ula.Rxd).IsFalse();
        await Assert.That(ula.DcdBar).IsFalse();

        FeedTone(ula, 512, 2);
        await Assert.That(ula.Rxd).IsTrue();
        await Assert.That(ula.DcdBar).IsFalse();
    }

    [Test]
    public async Task Carrier_detect_blips_off_when_the_leader_ends_and_is_back_for_the_data()
    {
        var ula = Create(0x00);
        FeedTone(ula, 512, 400);

        var lowAfterLeader = false;
        var blipClocks = 0;

        // The first 1200Hz cycle of the data.
        for (var clock = 0; clock < 1536; clock++)
        {
            ula.CasIn = clock % 1024 < 512;
            Clock(ula);

            if (ula.DcdBar)
            {
                blipClocks++;
            }
        }

        lowAfterLeader = !ula.DcdBar;

        await Assert.That(blipClocks).IsBetween(240, 260);
        await Assert.That(lowAfterLeader).IsTrue();
    }

    [Test]
    public async Task Carrier_detect_drops_when_the_signal_disappears()
    {
        var ula = Create(0x00);
        FeedTone(ula, 512, 400);
        await Assert.That(ula.DcdBar).IsFalse();

        for (var clock = 0; clock < 4000; clock++)
        {
            ula.CasIn = false;
            Clock(ula);
        }

        await Assert.That(ula.DcdBar).IsTrue();
    }

    [Test]
    public async Task With_no_tape_signal_RxD_idles_high()
    {
        var ula = Create(0x00);
        FeedTone(ula, 1024, 3);

        for (var clock = 0; clock < 4000; clock++)
        {
            ula.CasIn = false;
            Clock(ula);
        }

        await Assert.That(ula.Rxd).IsTrue();
    }

    [Test]
    public async Task In_RS423_mode_the_cassette_is_disconnected()
    {
        var ula = Create(0x40);
        ula.Rs423In = false;

        FeedTone(ula, 512, 400);

        await Assert.That(ula.Rxd).IsFalse();
        await Assert.That(ula.DcdBar).IsTrue();
        await Assert.That(ula.CasOut).IsFalse();
    }
}
