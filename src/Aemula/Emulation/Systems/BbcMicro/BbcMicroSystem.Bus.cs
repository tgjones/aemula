namespace Aemula.Emulation.Systems.BbcMicro;

public sealed partial class BbcMicroSystem
{
    // The 6850 ACIA (IC4) is not modelled yet. The OS's interrupt handler
    // reads its status register first, to see whether it raised the
    // interrupt, so an open bus (which would return the instruction's own
    // address byte, &FE, with bit 7 set) sends it into an endless loop of
    // servicing an ACIA interrupt that never clears. Its status as left by
    // a master reset - transmit data register empty, nothing else - is what
    // an idle ACIA would answer.
    private const byte AciaStatusAfterReset = 0x02;

    // The decoded selects, active low (the "Bar" suffix) except where noted.
    // Net names are the netlist's.
    private bool _twoMHzE;
    private bool _fredBar;
    private bool _jimBar;
    private bool _sheilaBar;
    private bool _viaABar;
    private bool _viaBBar;
    private bool _adcBar;
    private bool _vidprocBar = true;
    private bool _romselBar;
    private bool _intonBar;
    private bool _crtcBar;
    private bool _aciaBar;
    private bool _intoffBar;
    private bool _romOeBar;

    // High while any 1MHz-bus device is addressed (_N077, IC23's output):
    // FRED, JIM, SHEILA &FE00-&FE1F, either VIA or the ADC.
    private bool _oneMHzDeviceSelected;

    // The sideways ROM sockets' chip enables, active low, indexed as
    // _socketRoms.
    private readonly bool[] _socketEnableBar = new bool[4];

    // Evaluates the address decode for the address and R/W as the board sees
    // them, with the CPU's clock in the given phase.
    //
    // The solder links the netlist leaves open are taken as a stock Model B
    // has them: S15/S16 and S17 joined (so FRED and JIM are 1MHz-bus
    // regions), the sideways ROM sockets' chip enables on LK35-LK38 and S21
    // joined across the second and fourth pads (sockets answer at
    // &8000-&BFFF). S18 and S19 are open, so their IC23 inputs float high.
    private void DecodeBus(bool phi1)
    {
        var address = _busAddress;

        bool Address(int bit) => (address & (1 << bit)) != 0;

        // IC37's fifth inverter makes 2MHZE from PHI1.
        _ic37.A5 = phi1;
        _twoMHzE = _ic37.Y5;

        // IC21: the second and third gates give the "&8000-&BFFF" strobe that
        // enables the sideways ROM sockets' decoder through S21.
        _ic21.A3 = true;
        _ic21.B3 = Address(14);
        _ic21.A4 = Address(15);
        _ic21.B4 = _ic21.Y3;
        var romPageBar = _ic21.Y4;

        // IC22: low for &FC00-&FFFF, the page of 1MHz-bus and I/O addresses
        // (and the top of the OS ROM).
        _ic22.A = Address(11);
        _ic22.B = true;
        _ic22.C = Address(15);
        _ic22.D = true;
        _ic22.E = Address(14);
        _ic22.F = Address(13);
        _ic22.G = Address(12);
        _ic22.H = Address(10);
        var ioPageBar = _ic22.Y;

        // IC20, first half: FRED (&FC), JIM (&FD) and SHEILA (&FE).
        _ic20.G1 = ioPageBar;
        _ic20.A1 = Address(8);
        _ic20.B1 = Address(9);
        _fredBar = _ic20.Y1_0;
        _jimBar = _ic20.Y1_1;
        _sheilaBar = _ic20.Y1_2;

        // IC24: SHEILA's eight 32-byte blocks.
        _ic24.A = Address(5);
        _ic24.B = Address(6);
        _ic24.C = Address(7);
        _ic24.G1 = Address(9);
        _ic24.G2A = Address(8);
        _ic24.G2B = ioPageBar;
        var sheila00Bar = _ic24.Y0;
        var sheila20Bar = _ic24.Y1;
        _viaABar = _ic24.Y2;
        _viaBBar = _ic24.Y3;
        _adcBar = _ic24.Y6;

        // IC26, first half: the write-only and read-only registers of
        // &FE20-&FE3F, split by A4 and R/W.
        _ic26.G1 = sheila20Bar;
        _ic26.A1 = Address(4);
        _ic26.B1 = _busRw;
        _vidprocBar = _ic26.Y1_0;
        _romselBar = _ic26.Y1_1;
        _intonBar = _ic26.Y1_2;

        // Second half: &FE00-&FE1F in four 8-byte blocks, only while the CPU's
        // clock is in PHI2 (IC29's first OR gate).
        _ic29.A1 = phi1;
        _ic29.B1 = sheila00Bar;
        _ic26.G2 = _ic29.Y1;
        _ic26.A2 = Address(3);
        _ic26.B2 = Address(4);
        _crtcBar = _ic26.Y2_0;
        _aciaBar = _ic26.Y2_1;
        _intoffBar = _ic26.Y2_3;

        // IC23: any 1MHz-bus device at all.
        _ic23.A = true;
        _ic23.B = _fredBar;
        _ic23.C = true;
        _ic23.D = _jimBar;
        _ic23.E = sheila00Bar;
        _ic23.F = _adcBar;
        _ic23.G = _viaBBar;
        _ic23.H = _viaABar;
        _oneMHzDeviceSelected = _ic23.Y;

        // IC25: the ROMs' output enable. It is withheld in FRED, JIM and
        // SHEILA, and outside PHI2.
        _ic25.A1 = _twoMHzE;
        _ic25.B1 = _sheilaBar;
        _ic25.C1 = _jimBar;
        _ic25.D1 = _fredBar;
        _romOeBar = _ic25.Y1;

        // IC20, second half: the ROM number latch's low two bits pick one of
        // the four sockets.
        _ic20.G2 = romPageBar;
        _ic20.A2 = _ic76.Qa;
        _ic20.B2 = _ic76.Qb;
        _socketEnableBar[0] = _ic20.Y2_0;
        _socketEnableBar[1] = _ic20.Y2_1;
        _socketEnableBar[2] = _ic20.Y2_2;
        _socketEnableBar[3] = _ic20.Y2_3;
    }

    // The CPU's clock has gone high: PHI2 begins, the address is valid, and a
    // write's data is on the bus.
    private void OnCpuPhi2Rising()
    {
        Cpu.Phi0 = true;

        DecodeBus(phi1: false);

        var address = _busAddress;

        if (!_busRw)
        {
            if ((address & 0x8000) == 0)
            {
                _ram[address] = Cpu.Data;
            }

            return;
        }

        if ((address & 0x8000) == 0)
        {
            Cpu.Data = _ram[address];
            return;
        }

        if (!_romOeBar)
        {
            var rom = SelectedRom(address);
            if (rom != null)
            {
                Cpu.Data = rom[address & 0x3FFF];
                return;
            }
        }

        if (!_intoffBar)
        {
            Cpu.Data = ReadStationId();
        }

        // Anything else that is not on the 1MHz bus's own clock leaves the
        // data bus undriven, which keeps the byte it last carried.
    }

    // The ROM, if any, that a read at an address in &8000-&FFFF finds enabled.
    private byte[]? SelectedRom(ushort address)
    {
        if ((address & 0xC000) == 0xC000)
        {
            return _osRom;
        }

        for (var socket = 0; socket < _socketEnableBar.Length; socket++)
        {
            if (!_socketEnableBar[socket])
            {
                return _socketRoms[socket];
            }
        }

        return null;
    }

    // IC96 puts the Econet station number on the data bus on a read of
    // &FE18-&FE1F. Each of its inputs is pulled up by RP2, and the links that
    // would pull them down are not made.
    private byte ReadStationId()
    {
        _ic96.G1 = _intoffBar;
        _ic96.G2 = _intoffBar;

        byte value = 0;

        // The data bit each output is wired to.
        if (_ic96.Y4 == true) value |= 0x01;
        if (_ic96.Y5 == true) value |= 0x02;
        if (_ic96.Y3 == true) value |= 0x04;
        if (_ic96.Y6 == true) value |= 0x08;
        if (_ic96.Y2 == true) value |= 0x10;
        if (_ic96.Y7 == true) value |= 0x20;
        if (_ic96.Y1 == true) value |= 0x40;
        if (_ic96.Y8 == true) value |= 0x80;

        return value;
    }

    // PHI2 ends: the cycle's data is captured by whatever latches on this
    // edge, and the CPU moves on to the next address.
    private void OnCpuPhi2Falling()
    {
        Cpu.Phi0 = false;
        _busHoldTicks = AddressDelayTicks;
    }

    // 1MHZE is the 1MHz bus clock, the E pin of the 6845 and the phi2 of the
    // VIAs. Those devices decide whether they are selected as E rises (the
    // synchroniser guarantees the CPU's PHI2 is already in progress for any
    // access to them), present a read's data then, and latch a write's data as
    // E falls with the same selection.
    private void OnOneMHzEnableRising()
    {
        DecodeBus(Cpu.Phi1);

        var address = _busAddress;

        SystemVia.Cs1 = _twoMHzE;
        SystemVia.Cs2 = _viaABar;
        SystemVia.RS = (byte)(address & 0x0F);
        SystemVia.RW = _busRw;
        SystemVia.Phi2 = true;

        UserVia.Cs1 = _twoMHzE;
        UserVia.Cs2 = _viaBBar;
        UserVia.RS = (byte)(address & 0x0F);
        UserVia.RW = _busRw;
        UserVia.Phi2 = true;

        Crtc.Cs = _crtcBar;
        Crtc.RS = (address & 1) != 0;
        Crtc.RW = _busRw;
        Crtc.E = true;

        if (!_busRw)
        {
            return;
        }

        if (_twoMHzE && !_viaABar)
        {
            Cpu.Data = SystemVia.DB;
        }
        else if (_twoMHzE && !_viaBBar)
        {
            Cpu.Data = UserVia.DB;
        }
        else if (!_crtcBar)
        {
            Cpu.Data = Crtc.DB;
        }
        else if (!_aciaBar)
        {
            Cpu.Data = AciaStatusAfterReset;
        }
    }

    private void OnOneMHzEnableFalling()
    {
        var data = Cpu.Data;

        SystemVia.DB = data;
        SystemVia.Phi2 = false;

        UserVia.DB = data;
        UserVia.Phi2 = false;

        Crtc.DB = data;
        Crtc.E = false;
    }
}
