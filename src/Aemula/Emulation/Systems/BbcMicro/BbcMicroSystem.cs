using System;
using System.IO;
using Aemula.Emulation.Chips;
using Aemula.Emulation.Chips.Mos6502;
using Aemula.Emulation.Chips.Mos6522;
using Aemula.Emulation.Chips.Motorola6845;
using Aemula.Emulation.Chips.Saa5050;
using Aemula.Emulation.Chips.VideoUla;
using Aemula.Emulation.Output;

namespace Aemula.Emulation.Systems.BbcMicro;

/// <summary>
/// The BBC Micro Model B, Issue 7 board, wired from
/// <c>docs/bbc-netlist.txt</c>.
///
/// One tick is one cycle of the 16MHz crystal oscillator (X1). The video ULA
/// divides it into the board's 8MHz, 4MHz and 2MHz clocks; the glue logic that
/// turns the 2MHz clock into the 6502's clock, stretches it for accesses to
/// the 1MHz bus, decodes addresses and latches the paged-ROM number is the
/// board's own 74xx logic, evaluated net by net.
///
/// What is not on the board's wires yet: the 6850 ACIA, serial ULA, 8271 FDC,
/// µPD7002 ADC, SN76489 and keyboard are absent, so their register
/// ranges read as an open bus and their interrupt lines stay inactive.
/// </summary>
public sealed partial class BbcMicroSystem : EmulatedSystem
{
    public override ulong CyclesPerSecond => 16_000_000;

    // The reset button's 555 (IC16) is a monostable: R21 (1M) and C8 (100n)
    // give a pulse of 1.1 * R * C = 110ms, in ticks of the 16MHz clock.
    private const uint ResetPulseTicks = 1_760_000;

    public readonly Mos6502Chip Cpu;

    // IC3 and IC69.
    public readonly Mos6522Chip SystemVia;
    public readonly Mos6522Chip UserVia;

    // IC2.
    public readonly Motorola6845Chip Crtc;

    // IC6.
    internal readonly VideoUlaChip VideoUla;

    // IC5.
    internal readonly Saa5050Chip Teletext;

    // IC51: the OS ROM socket. Its two programmed chip-select inputs are
    // wired to A15 and A14, so it answers at &C000-&FFFF only.
    private readonly byte[] _osRom = new byte[0x4000];

    // IC52, IC88, IC100 and IC101: the sideways ROM sockets, in the order of
    // the outputs of the IC20 decoder that enables them (LK35-LK38). A socket
    // is null while it is empty.
    private readonly byte[]?[] _socketRoms = new byte[4][];

    // IC16: reset's 555.
    private readonly Ne555Chip _resetTimer;

    // Address decode.
    private readonly Ttl74139Chip _ic20;
    private readonly Ttl7400Chip _ic21;
    private readonly Ttl7430Chip _ic22;
    private readonly Ttl7430Chip _ic23;
    private readonly Ttl74138Chip _ic24;
    private readonly Ttl7420Chip _ic25;
    private readonly Ttl74139Chip _ic26;
    private readonly Ttl74163Chip _ic76;
    private readonly Ttl74244Chip _ic96;

    // Interrupts, reset and the addressable latch.
    private readonly Ttl7438Chip _ic27;
    private readonly Ttl7474Chip _ic97;
    private readonly Ttl7404Chip _ic33;
    internal readonly Ttl74259Chip _ic32;

    // The screen address's wrap at the end of screen memory (hardware
    // scroll); IC27's fourth gate is the other part of it.
    private readonly Ttl7410Chip _ic36;
    private readonly Ttl74283Chip _ic39;
    private readonly Ttl7400Chip _ic40;

    // DISEN: the 6845's display enable, blanked from raster line 8 on.
    private readonly Ttl7486Chip _ic38;
    private readonly Ttl7402Chip _ic41;

    // The 6502's clock and its synchronisation to the 1MHz bus.
    private readonly Ttl74LS51Chip _ic28;
    private readonly Ttl7432Chip _ic29;
    private readonly Ttl7474Chip _ic30;
    private readonly Ttl7474Chip _ic31;
    private readonly Ttl7474Chip _ic34;
    private readonly Ttl7404Chip _ic37;

    // The teletext data path: IC15 latches the video bus for the SAA5050.
    private readonly Ttl74273Chip _ic15;

    public BbcMicroSystem(TelevisionStandard? standard = null)
        : this(standard, ResetPulseTicks)
    {
    }

    internal BbcMicroSystem(TelevisionStandard? standard, uint resetPulseTicks)
        : base(new Television(standard ?? TelevisionStandard.Pal, 16_000_000))
    {
        Cpu = new Mos6502Chip(Mos6502Options.Default);

        SystemVia = new Mos6522Chip();
        UserVia = new Mos6522Chip();
        Crtc = new Motorola6845Chip();
        VideoUla = new VideoUlaChip();
        Teletext = new Saa5050Chip();

        _ic20 = new Ttl74139Chip();
        _ic21 = new Ttl7400Chip();
        _ic22 = new Ttl7430Chip();
        _ic23 = new Ttl7430Chip();
        _ic24 = new Ttl74138Chip();
        _ic25 = new Ttl7420Chip();
        _ic26 = new Ttl74139Chip();
        _ic76 = new Ttl74163Chip();
        _ic96 = new Ttl74244Chip();
        _ic27 = new Ttl7438Chip();
        _ic97 = new Ttl7474Chip();
        _ic33 = new Ttl7404Chip();
        _ic32 = new Ttl74259Chip();
        _ic36 = new Ttl7410Chip();
        _ic39 = new Ttl74283Chip();
        _ic40 = new Ttl7400Chip();
        _ic38 = new Ttl7486Chip();
        _ic41 = new Ttl7402Chip();
        _ic28 = new Ttl74LS51Chip();
        _ic29 = new Ttl7432Chip();
        _ic30 = new Ttl7474Chip();
        _ic31 = new Ttl7474Chip();
        _ic34 = new Ttl7474Chip();
        _ic37 = new Ttl7404Chip();
        _ic15 = new Ttl74273Chip();

        _ic8 = new Ttl8195Chip();
        _ic9 = new Ttl8195Chip();
        _ic10 = new Ttl8195Chip();
        _ic11 = new Ttl8195Chip();
        _ic12 = new Ttl8195Chip();
        _ic13 = new Ttl8195Chip();
        _ic43 = new Ttl7404Chip();
        _ic44 = new Ttl7474Chip();
        _ic45 = new Ttl74139Chip();

        _resetTimer = new Ne555Chip { PulseTicks = resetPulseTicks };

        // Nothing is attached to the ports of either VIA. A floating TTL
        // input reads high, except where the board or the (absent)
        // keyboard pulls it low: the keyboard drives PA7 low while no key is
        // pressed, and CA2 (its key-pressed interrupt) low.
        SystemVia.PA = 0x00;
        SystemVia.PB = 0xFF;
        SystemVia.Ca2 = false;

        // CB1 is the ADC's end-of-conversion line (pulled up) and CB2 the
        // light pen strobe.
        SystemVia.Cb1 = true;
        SystemVia.Cb2 = false;

        // The user VIA drives the printer port through IC70 and the user port
        // connector directly; CA1 is the printer's acknowledge line, pulled
        // up.
        UserVia.PA = 0xFF;
        UserVia.PB = 0xFF;
        UserVia.Ca1 = true;
        UserVia.Cb1 = true;
        UserVia.Cb2 = true;

        // Power-on: C24 holds the 555's trigger low while it charges.
        _resetTimer.TriggerBar = false;
        _resetTimer.TriggerBar = true;

        // IC96's inputs are the station-number links, none of which is made.
        _ic96.A1 = true;
        _ic96.A2 = true;
        _ic96.A3 = true;
        _ic96.A4 = true;
        _ic96.A5 = true;
        _ic96.A6 = true;
        _ic96.A7 = true;
        _ic96.A8 = true;

        _busAddress = Cpu.Address;
        _busRw = Cpu.RW;

        LoadRoms();
    }

    private void LoadRoms()
    {
        var romsDirectory = Path.Combine(AppContext.BaseDirectory, "Emulation", "Systems", "BbcMicro", "Roms");

        using (var osStream = File.OpenRead(Path.Combine(romsDirectory, "os.rom")))
        {
            osStream.ReadExactly(_osRom);
        }

        // The latch that selects a paged ROM is decoded on its low two bits
        // only (IC20 takes QA and QB), so each socket answers to four ROM
        // numbers. BASIC sits where ROM number 15 selects it, as on a stock
        // machine, and so also answers to 3, 7 and 11.
        _socketRoms[3] = new byte[0x4000];

        using var basicStream = File.OpenRead(Path.Combine(romsDirectory, "BASIC.rom"));
        basicStream.ReadExactly(_socketRoms[3]!);
    }

    /// <summary>
    /// BREAK: pulses the 555's trigger, as the BREAK key does through R13/C24.
    /// </summary>
    public override void Reset()
    {
        _resetTimer.TriggerBar = false;
        _resetTimer.TriggerBar = true;
    }

    private int _crtcOutputs = -1;

    public override void Tick()
    {
        // The CPU's address and R/W outputs reach the rest of the board a
        // little after its clock changes (the 6502's address delay); see
        // _busAddress.
        if (_busHoldTicks > 0 && --_busHoldTicks == 0)
        {
            _busAddress = Cpu.Address;
            _busRw = Cpu.RW;
        }

        // The ULA's data bus carries the video byte the RAM put on BD for the
        // fetch at the next 16MHz edge (the 8MHz, 4MHz and 2MHz outputs all
        // low), and otherwise the CPU's data, which the ULA takes for a
        // register write.
        if (!VideoUla.Clock8MHz && !VideoUla.Clock4MHz && !VideoUla.Clock2MHz)
        {
            VideoUla.Data = _bd;
        }
        else if (!_vidprocBar)
        {
            VideoUla.Data = Cpu.Data;
        }

        StepTeletext();

        VideoUla.A0 = (_busAddress & 1) != 0;
        VideoUla.CS = _vidprocBar;

        VideoUla.Clk16MHz = true;
        VideoUla.Clk16MHz = false;
        _resetTimer.Tick();

        Crtc.Clk = VideoUla.CrtcClock;

        // IC38's gate (the other input tied to +5V) inverts DISPEN, and IC41's
        // NORs that with RA3. The 6845's outputs move only when it is clocked,
        // so the gates are evaluated only when one of them has changed.
        var dispEn = Crtc.DispEn;
        var cursor = Crtc.Cursor;
        var vSync = Crtc.VSync;
        var ra3 = (Crtc.RA & 8) != 0;
        var crtcOutputs = (dispEn ? 1 : 0) | (cursor ? 2 : 0) | (vSync ? 4 : 0) | (ra3 ? 8 : 0);

        if (crtcOutputs != _crtcOutputs)
        {
            _crtcOutputs = crtcOutputs;

            _ic38.A1 = true;
            _ic38.B1 = dispEn;
            _ic41.A3 = _ic38.Y1;
            _ic41.B3 = ra3;
            VideoUla.Disen = _ic41.Y3;
            VideoUla.Cursor = cursor;
            SystemVia.Ca1 = vSync;
        }

        Settle();
        StepRam();
        TickCompositeVideo();
    }

    // Reads and writes without side effects, for tests and the debugger.
    internal byte ReadByteDebug(ushort address)
    {
        if ((address & 0x8000) == 0)
        {
            return ReadRam(address);
        }

        if ((address & 0xC000) == 0xC000)
        {
            return _osRom[address & 0x3FFF];
        }

        var socket = (_ic76.Qa ? 1 : 0) | (_ic76.Qb ? 2 : 0);
        return _socketRoms[socket]?[address & 0x3FFF] ?? 0xFF;
    }

    internal void WriteByteDebug(ushort address, byte value)
    {
        if ((address & 0x8000) == 0)
        {
            WriteRam(address, value);
        }
    }
}
