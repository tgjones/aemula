using Aemula.Emulation.Chips;

namespace Aemula.Emulation.Systems.BbcMicro;

public sealed partial class BbcMicroSystem
{
    // The RAM is shared by the CPU and the video circuits without either
    // waiting for the other. The 4MHz clock is split into a /RAS phase and a
    // /CAS phase, and the 2MHz clock gives the CPU one pair of them and the
    // video ULA the next:
    //
    //   count (16MHz ticks, as the ULA's counter)   0  1  2  3  4  5  6  7
    //   2MHz clock                                  .  .  .  .  #  #  #  #
    //   4MHz clock                                  .  .  #  #  .  .  #  #
    //   /RAS (4MHz sampled by the 8MHz clock)       #  .  .  #  #  .  .  #
    //   /CAS                                        #  #  .  .  #  #  .  .
    //
    // IC45 enables one of four address buffers while the 4MHz clock is low.
    // /RAS is still high for its first tick, so the first buffer enabled puts
    // the row address on RA0-RA6 (the CPU's A0-A6, or the video's low
    // address bits); /RAS falls and the DRAMs latch it; /RAS being low then
    // selects the second buffer, with the column address (A7-A13, or the
    // video's high bits). The 4MHz clock rising disables the buffers and pulls
    // /CAS low in the same tick, so the DRAMs latch the column address from
    // the lines the buffers were driving a tick before.
    //
    // The CPU's phi2 straddles the end of its slot, so a read's data is on the
    // RAM data bus (BD) from /CAS falling and passes through IC14 to the CPU's
    // data bus while phi2 is high; a write's data goes the other way. The video
    // ULA takes its byte from BD on the clock edge after the video /CAS ends.
    // The bus holds its last value while nothing drives it.

    // The 16 HM4816s (IC53-IC68) as two banks of eight. Each bank has its own
    // /CAS: IC61-IC68 (bank 0) take /CAS0 for RAM addresses below &4000,
    // IC53-IC60 (bank 1) /CAS1 for the rest. The chip at index n of a bank
    // holds bit n of every byte.
    private readonly Hm4816BankChip[] _dram = [new(), new()];

    // The address buffers: IC8 and IC9 for the video address in the bitmap
    // modes, IC10 and IC11 in Mode 7, IC12 and IC13 for the CPU. Their eight
    // outputs are RA0-RA6 and WE#.
    private Ttl8195Chip _ic8;
    private Ttl8195Chip _ic9;
    private Ttl8195Chip _ic10;
    private Ttl8195Chip _ic11;
    private Ttl8195Chip _ic12;
    private Ttl8195Chip _ic13;

    // IC45 selects the buffers and /CAS; IC44's first flip-flop makes /RAS
    // from the 4MHz clock, through IC43's sixth inverter.
    private Ttl74139Chip _ic45;
    private Ttl7474Chip _ic44;
    private Ttl7404Chip _ic43;

    // The row/column address lines, WE# and the RAM data bus as last driven.
    private byte _ra;
    private bool _weBar = true;
    private byte _bd;

    // The byte on the RAM data bus, for tests.
    internal byte DataBus => _bd;

    private bool _rasBar = true;
    private bool _casBar0 = true;
    private bool _casBar1 = true;

    // The video address adder's outputs depend on MA8-MA12 and the hardware
    // scroll size bits only, so they are worked out again only when those
    // change.
    private int _videoAdderKey = -1;
    private int _videoAdder;

    private int VideoAdderOutput()
    {
        var ma = Crtc.MA;
        var key = ((ma >> 8) & 0x1F) | (_ic32.Q4 ? 0x20 : 0) | (_ic32.Q5 ? 0x40 : 0);

        if (key != _videoAdderKey)
        {
            _videoAdderKey = key;
            _videoAdder = VideoAdder(ma);
        }

        return _videoAdder;
    }

    // IC14, the buffer between the RAM data bus and the CPU's, is enabled
    // while phi2 is high for any RAM address, and for the video ULA's
    // registers, whose data bus is BD.
    private bool CpuDataBufferEnabled()
    {
        // IC21's second gate is low for any address from &8000 up except a
        // write to the video ULA; IC25's second gate turns that, phi2 and
        // 2MHZE into IC14's enable.
        _ic21.A2 = (_busAddress & 0x8000) != 0;
        _ic21.B2 = _vidprocBar;
        _ic25.A2 = _ic21.Y2;
        _ic25.B2 = Cpu.Phi2;
        _ic25.C2 = _twoMHzE;
        _ic25.D2 = true;
        return !_ic25.Y2;
    }

    private void StepRam()
    {
        var c2 = VideoUla.Clock2MHz;
        var c4 = VideoUla.Clock4MHz;
        var c8 = VideoUla.Clock8MHz;

        // /RAS and the /CAS strobes are a function of these inputs alone (and
        // IC44, which only moves on an 8MHz edge), and on most ticks none of
        // them has changed, so the gates are evaluated only when one does.
        var key = (c2 ? 0x01 : 0)
            | (c4 ? 0x02 : 0)
            | (c8 ? 0x04 : 0)
            | (_vidprocBar ? 0x08 : 0)
            | (_ic30.Q2 ? 0x10 : 0)
            | (_ic30.Qn2 ? 0x20 : 0)
            | ((_busAddress & 0x4000) != 0 ? 0x40 : 0)
            | ((VideoAdderOutput() & 8) != 0 ? 0x80 : 0);

        if (key != _ramStrobeKey)
        {
            _ramStrobeKey = key;
            EvaluateRamStrobes(c2, c4, c8);
        }

        SelectAddressBuffers(c2, c4);
    }

    private int _ramStrobeKey = -1;

    private void EvaluateRamStrobes(bool c2, bool c4, bool c8)
    {
        _ic44.D1 = c4;
        _ic44.Clk1 = c8;
        _ic43.A6 = _ic44.Qn1;
        var rasBar = _ic43.Y6;

        // IC41's first gate withholds /CAS during a CPU slot in which the
        // video ULA's registers are written, so the RAM does not store the
        // write. The bank is A14, or the video address's bit 14 (VAA3) during
        // the video slot, inverted by IC28's second gate.
        _ic41.A1 = c2;
        _ic41.B1 = _vidprocBar;
        _ic28.A2 = _ic30.Q2;
        _ic28.B2 = (VideoAdderOutput() & 8) != 0;
        _ic28.C2 = _ic30.Qn2;
        _ic28.D2 = (_busAddress & 0x4000) != 0;
        _ic45.G1 = _ic41.Y1;
        _ic45.A1 = c4;
        _ic45.B1 = _ic28.Y2;
        var casBar0 = _ic45.Y1_3;
        var casBar1 = _ic45.Y1_1;

        // The strobes see the address lines as they were before this tick.
        if (rasBar != _rasBar)
        {
            _rasBar = rasBar;

            foreach (var bank in _dram)
            {
                bank.Address = _ra;
                bank.RasBar = rasBar;
            }
        }

        if (casBar0 != _casBar0)
        {
            _casBar0 = casBar0;
            StrobeCas(0, casBar0);
        }

        if (casBar1 != _casBar1)
        {
            _casBar1 = casBar1;
            StrobeCas(1, casBar1);
        }
    }

    private void StrobeCas(int bankIndex, bool casBar)
    {
        var bank = _dram[bankIndex];

        if (casBar)
        {
            bank.CasBar = true;
            return;
        }

        var enabled = CpuDataBufferEnabled();

        bank.Address = _ra;
        bank.WeBar = _weBar;

        // A write's data comes from the CPU through IC14.
        if (enabled && !_busRw)
        {
            _bd = Cpu.Data;
        }

        bank.Din = _bd;
        bank.CasBar = false;

        if (!_weBar)
        {
            return;
        }

        var value = bank.Dout ?? _bd;
        _bd = value;

        if (enabled && _busRw)
        {
            Cpu.Data = value;
        }
    }

    // IC45's second half decodes the 2MHz clock and /RAS into one of four
    // buffers while the 4MHz clock is low: the CPU's row and column, or the
    // video's.
    private void SelectAddressBuffers(bool c2, bool c4)
    {
        _ic45.G2 = c4;
        _ic45.A2 = _rasBar;
        _ic45.B2 = c2;

        var cpuColumn = !_ic45.Y2_0;
        var cpuRow = !_ic45.Y2_1;
        var videoColumn = !_ic45.Y2_2;
        var videoRow = !_ic45.Y2_3;

        if (!cpuColumn && !cpuRow && !videoColumn && !videoRow)
        {
            return;
        }

        var address = _busAddress;
        var ma = Crtc.MA;

        // TTXVDU is MA13; IC38's fourth gate inverts it.
        var teletext = (ma & 0x2000) != 0;

        bool Ma(int bit) => (ma & (1 << bit)) != 0;
        bool Ra(int bit) => (Crtc.RA & (1 << bit)) != 0;
        bool Address(int bit) => (address & (1 << bit)) != 0;

        byte? ra = null;
        bool? we = null;

        if (cpuRow)
        {
            _ic12.G1 = false;
            _ic12.G2 = false;
            _ic12.A1 = Address(0);
            _ic12.A2 = Address(1);
            _ic12.A3 = Address(2);
            _ic12.A4 = Address(3);
            _ic12.A5 = Address(4);
            _ic12.A6 = Address(5);
            _ic12.A7 = Address(6);
            _ic12.A8 = true;
            ra = Pack(_ic12);
            we = _ic12.Y8;
        }
        else if (cpuColumn)
        {
            _ic13.G1 = false;
            _ic13.G2 = false;
            _ic13.A1 = Address(7);
            _ic13.A2 = Address(8);
            _ic13.A3 = Address(9);
            _ic13.A4 = Address(10);
            _ic13.A5 = Address(11);
            _ic13.A6 = Address(12);
            _ic13.A7 = Address(13);

            // WE# is low for a write to RAM: IC29's second gate ORs A15 with
            // R/W.
            _ic29.A2 = Address(15);
            _ic29.B2 = _busRw;
            _ic13.A8 = _ic29.Y2;
            ra = Pack(_ic13);
            we = _ic13.Y8;
        }
        else if (videoRow)
        {
            if (!teletext)
            {
                // IC8: RA0-RA2 and the first four bits of MA.
                _ic8.G1 = false;
                _ic8.G2 = false;
                _ic8.A1 = Ra(0);
                _ic8.A2 = Ra(1);
                _ic8.A3 = Ra(2);
                _ic8.A4 = Ma(0);
                _ic8.A5 = Ma(1);
                _ic8.A6 = Ma(2);
                _ic8.A7 = Ma(3);
                _ic8.A8 = true;
                ra = Pack(_ic8);
                we = _ic8.Y8;
            }
            else
            {
                // IC10: MA0-MA5, and MA6 exclusive-ORed by IC38's third gate
                // with the inverted 1MHz clock (_N352). The video ULA's byte
                // comes from the slot in which that clock is low, so it is
                // MA6 itself; the other slot, which nothing reads at 1MHz,
                // fetches the neighbouring row.
                _ic10.G1 = false;
                _ic10.G2 = false;
                _ic10.A1 = Ma(0);
                _ic10.A2 = Ma(1);
                _ic10.A3 = Ma(2);
                _ic10.A4 = Ma(3);
                _ic10.A5 = Ma(4);
                _ic10.A6 = Ma(5);
                _ic37.A3 = VideoUla.Clock1MHz;
                _ic38.A3 = Ma(6);
                _ic38.B3 = _ic37.Y3;
                _ic10.A7 = _ic38.Y3;
                _ic10.A8 = true;
                ra = Pack(_ic10);
                we = _ic10.Y8;
            }
        }
        else if (videoColumn)
        {
            if (!teletext)
            {
                // IC9: MA4-MA7 and the adder's VAA0-VAA2.
                var vaa = VideoAdderOutput();
                _ic9.G1 = false;
                _ic9.G2 = false;
                _ic9.A1 = Ma(4);
                _ic9.A2 = Ma(5);
                _ic9.A3 = Ma(6);
                _ic9.A4 = Ma(7);
                _ic9.A5 = (vaa & 1) != 0;
                _ic9.A6 = (vaa & 2) != 0;
                _ic9.A7 = (vaa & 4) != 0;
                _ic9.A8 = true;
                ra = Pack(_ic9);
                we = _ic9.Y8;
            }
            else
            {
                // IC11: MA7-MA9 and the rest tied high, which places the
                // screen in the top 1K of a 16K bank.
                _ic11.G1 = false;
                _ic11.G2 = false;
                _ic11.A1 = Ma(7);
                _ic11.A2 = Ma(8);
                _ic11.A3 = Ma(9);
                _ic11.A4 = true;
                _ic11.A5 = true;
                _ic11.A6 = true;
                _ic11.A7 = true;
                _ic11.A8 = true;
                ra = Pack(_ic11);
                we = _ic11.Y8;
            }
        }

        if (ra.HasValue)
        {
            _ra = ra.Value;
        }

        if (we.HasValue)
        {
            _weBar = we.Value;
        }
    }

    private static byte Pack(in Ttl8195Chip chip) => (byte)(
        (chip.Y1 == true ? 0x01 : 0)
        | (chip.Y2 == true ? 0x02 : 0)
        | (chip.Y3 == true ? 0x04 : 0)
        | (chip.Y4 == true ? 0x08 : 0)
        | (chip.Y5 == true ? 0x10 : 0)
        | (chip.Y6 == true ? 0x20 : 0)
        | (chip.Y7 == true ? 0x40 : 0));

    private byte ReadRam(ushort address) =>
        _dram[(address >> 14) & 1].Peek(address & 0x3FFF);

    private void WriteRam(ushort address, byte value) =>
        _dram[(address >> 14) & 1].Poke(address & 0x3FFF, value);
}
