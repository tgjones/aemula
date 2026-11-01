using System.Collections.Generic;
using System.Threading.Tasks;
using Aemula.Emulation.Chips.Mos6850;

namespace Aemula.Tests.Emulation.Chips.Mos6850;

public class Mos6850ChipTests
{
    private const byte Control8N1Div16 = 0x15;
    private const byte Control8N1Div64 = 0x16;
    private const byte MasterReset = 0x03;

    private static Mos6850Chip Create()
    {
        var chip = new Mos6850Chip { Cs0 = true, Cs1 = true, Cs2Bar = false };
        chip.CtsBar = false;
        chip.DcdBar = false;
        return chip;
    }

    private static void Write(Mos6850Chip chip, bool data, byte value)
    {
        chip.RS = data;
        chip.RW = false;
        chip.DB = value;
        chip.E = true;
        chip.E = false;
    }

    private static byte Read(Mos6850Chip chip, bool data)
    {
        chip.RS = data;
        chip.RW = true;
        chip.E = true;
        var value = chip.DB;
        chip.E = false;
        return value;
    }

    // Clocks the transmitter for the given number of bit times at a divide
    // ratio, returning the line level sampled in the middle of each bit.
    private static List<bool> SampleTransmit(Mos6850Chip chip, int ratio, int bits)
    {
        var line = new List<bool>();

        for (var bit = 0; bit < bits; bit++)
        {
            for (var clock = 0; clock < ratio; clock++)
            {
                chip.Txc = true;
                chip.Txc = false;

                if (clock == ratio / 2)
                {
                    line.Add(chip.TxD);
                }
            }
        }

        return line;
    }

    private static void ReceiveBits(Mos6850Chip chip, int ratio, params bool[] bits)
    {
        foreach (var bit in bits)
        {
            chip.Rxd = bit;

            for (var clock = 0; clock < ratio; clock++)
            {
                chip.Rxc = true;
                chip.Rxc = false;
            }
        }
    }

    private static bool[] Frame(byte value, bool parityBit = false, bool hasParity = false, bool stop = true)
    {
        var bits = new List<bool> { false };

        for (var bit = 0; bit < 8; bit++)
        {
            bits.Add((value & (1 << bit)) != 0);
        }

        if (hasParity)
        {
            bits.Add(parityBit);
        }

        bits.Add(stop);
        return bits.ToArray();
    }

    [Test]
    public async Task After_a_master_reset_the_transmitter_is_empty_and_nothing_else_is_set()
    {
        var chip = Create();
        Write(chip, false, MasterReset);

        await Assert.That(Read(chip, false)).IsEqualTo((byte)0x02);
        await Assert.That(chip.Irq).IsTrue();
        await Assert.That(chip.RtsBar).IsTrue();
    }

    [Test]
    public async Task Status_shows_clear_to_send_and_carrier_lost_from_their_pins()
    {
        var chip = Create();
        Write(chip, false, MasterReset);
        Write(chip, false, Control8N1Div64);

        chip.DcdBar = true;
        chip.CtsBar = true;

        // Transmit empty is held low while CTS is high.
        await Assert.That(Read(chip, false)).IsEqualTo((byte)0x0C);
    }

    [Test]
    public async Task A_character_goes_out_as_a_start_bit_data_lsb_first_and_a_stop_bit()
    {
        var chip = Create();
        Write(chip, false, MasterReset);
        Write(chip, false, Control8N1Div16);
        Write(chip, true, 0xA5);

        await Assert.That(Read(chip, false) & 0x02).IsEqualTo(0);

        var line = SampleTransmit(chip, 16, 12);

        bool[] expected = [false, true, false, true, false, false, true, false, true, true, true, true];
        await Assert.That(line.ToArray()).IsEquivalentTo(expected);
        await Assert.That(Read(chip, false) & 0x02).IsEqualTo(0x02);
    }

    [Test]
    public async Task Word_format_picks_data_bits_parity_and_stop_bits()
    {
        var chip = Create();
        Write(chip, false, MasterReset);

        // 7 bits, even parity, 1 stop bit, divide by 16.
        Write(chip, false, 0x09);
        Write(chip, true, 0x03);

        var line = SampleTransmit(chip, 16, 11);

        // Start, 1100000 (0x03 LSB first), even parity bit 0, stop, then idle.
        bool[] expected = [false, true, true, false, false, false, false, false, false, true, true];
        await Assert.That(line.ToArray()).IsEquivalentTo(expected);
    }

    [Test]
    public async Task A_received_frame_is_available_to_read_and_reading_it_clears_the_flag()
    {
        var chip = Create();
        Write(chip, false, MasterReset);
        Write(chip, false, Control8N1Div64);

        ReceiveBits(chip, 64, Frame(0x5A));

        await Assert.That(Read(chip, false) & 0x01).IsEqualTo(0x01);
        await Assert.That(Read(chip, true)).IsEqualTo((byte)0x5A);
        await Assert.That(Read(chip, false) & 0x01).IsEqualTo(0);
    }

    [Test]
    public async Task A_receive_interrupt_follows_the_flag_when_enabled()
    {
        var chip = Create();
        Write(chip, false, MasterReset);
        Write(chip, false, (byte)(Control8N1Div64 | 0x80));

        await Assert.That(chip.Irq).IsTrue();

        ReceiveBits(chip, 64, Frame(0x01));

        await Assert.That(chip.Irq).IsFalse();
        await Assert.That(Read(chip, false) & 0x80).IsEqualTo(0x80);

        Read(chip, true);

        await Assert.That(chip.Irq).IsTrue();
    }

    [Test]
    public async Task A_transmit_interrupt_follows_the_empty_flag_when_enabled()
    {
        var chip = Create();
        Write(chip, false, MasterReset);
        Write(chip, false, (byte)(Control8N1Div64 | 0x20));

        await Assert.That(chip.Irq).IsFalse();

        Write(chip, true, 0x00);

        await Assert.That(chip.Irq).IsTrue();
    }

    [Test]
    public async Task A_missing_stop_bit_is_a_framing_error()
    {
        var chip = Create();
        Write(chip, false, MasterReset);
        Write(chip, false, Control8N1Div64);

        ReceiveBits(chip, 64, Frame(0xFF, stop: false));

        await Assert.That(Read(chip, false) & 0x10).IsEqualTo(0x10);
    }

    [Test]
    public async Task Parity_is_checked_on_receive()
    {
        var chip = Create();
        Write(chip, false, MasterReset);

        // 8 bits, even parity, 1 stop bit, divide by 64.
        Write(chip, false, 0x1A);

        // 0x01 has one bit set, so even parity needs a 1.
        ReceiveBits(chip, 64, Frame(0x01, parityBit: true, hasParity: true));
        await Assert.That(Read(chip, false) & 0x40).IsEqualTo(0);
        Read(chip, true);

        ReceiveBits(chip, 64, Frame(0x01, parityBit: false, hasParity: true));
        await Assert.That(Read(chip, false) & 0x40).IsEqualTo(0x40);
    }

    [Test]
    public async Task A_second_frame_arriving_before_the_first_is_read_is_an_overrun()
    {
        var chip = Create();
        Write(chip, false, MasterReset);
        Write(chip, false, Control8N1Div64);

        ReceiveBits(chip, 64, Frame(0x11));
        ReceiveBits(chip, 64, Frame(0x22));

        await Assert.That(Read(chip, false) & 0x21).IsEqualTo(0x21);
        await Assert.That(Read(chip, true)).IsEqualTo((byte)0x11);

        // Overrun clears once the status was read and then the data.
        await Assert.That(Read(chip, false) & 0x20).IsEqualTo(0);
    }

    [Test]
    public async Task A_glitch_shorter_than_half_a_bit_is_not_a_start_bit()
    {
        var chip = Create();
        Write(chip, false, MasterReset);
        Write(chip, false, Control8N1Div64);

        chip.Rxd = false;

        for (var clock = 0; clock < 10; clock++)
        {
            chip.Rxc = true;
            chip.Rxc = false;
        }

        ReceiveBits(chip, 64, true, true, true, true);

        await Assert.That(Read(chip, false) & 0x01).IsEqualTo(0);
    }

    [Test]
    public async Task Losing_the_carrier_sets_its_flag_until_status_and_data_are_read_with_it_back()
    {
        var chip = Create();
        Write(chip, false, MasterReset);
        Write(chip, false, (byte)(Control8N1Div64 | 0x80));

        chip.DcdBar = false;
        await Assert.That(Read(chip, false) & 0x04).IsEqualTo(0);

        chip.DcdBar = true;
        await Assert.That(chip.Irq).IsFalse();

        chip.DcdBar = false;
        await Assert.That(Read(chip, false) & 0x04).IsEqualTo(0x04);

        Read(chip, true);

        await Assert.That(Read(chip, false) & 0x04).IsEqualTo(0);
        await Assert.That(chip.Irq).IsTrue();
    }

    [Test]
    public async Task The_transmit_control_bits_drive_request_to_send()
    {
        var chip = Create();
        Write(chip, false, MasterReset);

        Write(chip, false, Control8N1Div64);
        await Assert.That(chip.RtsBar).IsFalse();

        Write(chip, false, (byte)(Control8N1Div64 | 0x40));
        await Assert.That(chip.RtsBar).IsTrue();
    }
}
