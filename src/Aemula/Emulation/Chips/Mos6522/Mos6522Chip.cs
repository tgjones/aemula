namespace Aemula.Emulation.Chips.Mos6522;

/// <summary>
/// 6522 chip, originally manufactured by MOS Technology.
///
/// Known as the VIA (Versatile Interface Adapter). It provides two 8-bit
/// bidirectional peripheral ports (A and B), four handshake lines
/// (CA1/CA2/CB1/CB2), two 16-bit interval timers, an 8-bit shift register
/// and a vectored interrupt flag/enable pair, all addressed through a
/// 6502-style bus (four register selects, R/W, phi2, two chip selects).
///
/// Timing model: a selected read is presented on <see cref="DB"/> when
/// <see cref="Phi2"/> rises (any read side effects, such as clearing
/// interrupt flags, happen then). Everything else advances on the falling
/// edge of <see cref="Phi2"/>: a selected write latches <see cref="DB"/>,
/// the timers and shift register take one step, and CA2/CB2 handshake and
/// pulse outputs change. One phi2 cycle is one timer count.
///
/// Timer 1 loaded with N by a write to T1C-H counts N, N-1 ... 0, FFFF and
/// sets its flag on the FFFF step, so the flag appears N+1 cycles after the
/// write; T1 reloads from its latch on the step after that (free-running or
/// not), giving a period of N+2 cycles. A flag-clearing register access in the
/// cycle the flag is set, or the one after it, does not clear it.
/// </summary>
public sealed class Mos6522Chip
{
    private const int Orb = 0x0;
    private const int Ora = 0x1;
    private const int Ddrb = 0x2;
    private const int Ddra = 0x3;
    private const int T1Cl = 0x4;
    private const int T1Ch = 0x5;
    private const int T1Ll = 0x6;
    private const int T1Lh = 0x7;
    private const int T2Cl = 0x8;
    private const int T2Ch = 0x9;
    private const int Sr = 0xA;
    private const int AcrRegister = 0xB;
    private const int PcrRegister = 0xC;
    private const int Ifr = 0xD;
    private const int Ier = 0xE;
    private const int OraNoHandshake = 0xF;

    private const byte FlagCa2 = 0x01;
    private const byte FlagCa1 = 0x02;
    private const byte FlagShiftRegister = 0x04;
    private const byte FlagCb2 = 0x08;
    private const byte FlagCb1 = 0x10;
    private const byte FlagTimer2 = 0x20;
    private const byte FlagTimer1 = 0x40;
    private const byte FlagAnyBit = 0x80;

    private const byte AcrLatchA = 0x01;
    private const byte AcrLatchB = 0x02;
    private const byte AcrT2PulseCount = 0x20;
    private const byte AcrT1FreeRun = 0x40;
    private const byte AcrT1Pb7 = 0x80;

    // Bus pins.

    /// <summary>
    /// Data bus pins (D0-D7).
    /// </summary>
    public byte DB { get; set; }

    /// <summary>
    /// Register select pins (RS0-RS3). Only the low four bits are used.
    /// </summary>
    public byte RS { private get; set; }

    /// <summary>
    /// Read/write pin (input). Read = true, write = false.
    /// </summary>
    public bool RW { private get; set; }

    /// <summary>
    /// Chip select 1 (input), active high.
    /// </summary>
    public bool Cs1 { private get; set; }

    /// <summary>
    /// Chip select 2 (input), active low.
    /// </summary>
    public bool Cs2 { private get; set; }

    private bool Selected => Cs1 && !Cs2;

    /// <summary>
    /// Interrupt request (output), active low, open drain on real hardware.
    /// True while no enabled interrupt flag is set.
    /// </summary>
    public bool Irq { get; private set; } = true;

    private bool _res;

    /// <summary>
    /// Reset pin (input), active low. Registers clear on the rising edge
    /// (once reset is released), matching <see cref="Mos6820.Mos6820Chip.Res"/>.
    /// The timers, their latches and the shift register are not affected,
    /// as on the real chip.
    /// </summary>
    public bool Res
    {
        get => _res;
        set
        {
            if (_res == value)
            {
                return;
            }

            _res = value;

            if (!value)
            {
                return;
            }

            _ora = 0;
            _orb = 0;
            _ddra = 0;
            _ddrb = 0;
            _acr = 0;
            _pcr = 0;
            _ifr = 0;
            _ier = 0;
            _ca2 = true;
            _cb2 = true;
            _cb1Out = true;
            _ca2Armed = false;
            _ca2Restore = false;
            _cb2Armed = false;
            _cb2Restore = false;
            _srActive = false;
            _t1Armed = false;
            _t2Armed = false;
            _t1Reload = false;
            _t2Reload = false;
            UpdateIrq();
        }
    }

    private bool _phi2;

    /// <summary>
    /// Clock input. See the class remarks for what happens on each edge.
    /// </summary>
    public bool Phi2
    {
        get => _phi2;
        set
        {
            if (_phi2 == value)
            {
                return;
            }

            _phi2 = value;

            if (value)
            {
                OnRisingEdge();
            }
            else
            {
                OnFallingEdge();
            }
        }
    }

    private void OnRisingEdge()
    {
        if (!Selected || !RW)
        {
            return;
        }

        var register = RS & 0x0F;

        DB = ReadRegister(register);

        if (register == Ora)
        {
            _ca2Armed = Ca2HandshakeOrPulse;
        }
        else if (register == Sr)
        {
            StartShifting();
        }
    }

    private void OnFallingEdge()
    {
        var flagsBefore = _ifr;

        var pb6 = (PortB & 0x40) != 0;
        var pb6Falling = _pb6Previous && !pb6;
        _pb6Previous = pb6;

        StepTimer1();
        StepTimer2(pb6Falling);

        if (SrMode is 2 or 6)
        {
            ClockShiftRegister();
        }

        _flagsSetThisEdge = (byte)(_ifr & ~flagsBefore);

        if (Selected && !RW)
        {
            var register = RS & 0x0F;

            WriteRegister(register, DB);

            if (register == Orb)
            {
                _cb2Armed = Cb2HandshakeOrPulse;
            }
            else if (register == Ora)
            {
                _ca2Armed = Ca2HandshakeOrPulse;
            }
            else if (register == Sr)
            {
                StartShifting();
            }
        }

        if (_ca2Armed)
        {
            _ca2Armed = false;
            _ca2 = false;
            _ca2Restore = Ca2PulseMode;
        }
        else if (_ca2Restore)
        {
            _ca2Restore = false;
            _ca2 = true;
        }

        if (_cb2Armed)
        {
            _cb2Armed = false;
            _cb2 = false;
            _cb2Restore = Cb2PulseMode;
        }
        else if (_cb2Restore)
        {
            _cb2Restore = false;
            _cb2 = true;
        }
    }

    // Interrupt flags.

    private byte _ifr;
    private byte _ier;

    // Flags the counters raised on the most recent falling edge. An access in
    // that edge's cycle or the next one cannot clear them.
    private byte _flagsSetThisEdge;

    private void SetFlag(byte flag)
    {
        _ifr |= flag;
        UpdateIrq();
    }

    private void ClearFlags(byte flags)
    {
        _ifr &= (byte)~(flags & ~_flagsSetThisEdge);
        UpdateIrq();
    }

    private void UpdateIrq()
    {
        Irq = (_ifr & _ier & 0x7F) == 0;
    }

    // Port A.

    private byte _ora;
    private byte _ddra;
    private byte _ira;

    /// <summary>
    /// Peripheral A port pins (input) - the level externally driven onto
    /// PA0-PA7, seen on the bits <see cref="PaDirection"/> marks as inputs.
    /// </summary>
    public byte PA { private get; set; }

    /// <summary>
    /// Output register A, i.e. the level driven on each pin whose
    /// <see cref="PaDirection"/> bit is set.
    /// </summary>
    public byte PaOutput => _ora;

    /// <summary>
    /// Data direction of PA0-PA7 (1 = output).
    /// </summary>
    public byte PaDirection => _ddra;

    /// <summary>
    /// The level the chip is presenting on PA0-PA7: <see cref="PA"/> on
    /// input bits, the output register on output bits. Side effect free.
    /// </summary>
    public byte PortA => (byte)((PA & ~_ddra) | (_ora & _ddra));

    // Port B.

    private byte _orb;
    private byte _ddrb;
    private byte _irb;
    private bool _pb6Previous;

    /// <summary>
    /// Peripheral B port pins (input) - see <see cref="PA"/>.
    /// </summary>
    public byte PB { private get; set; }

    /// <summary>
    /// Output register B, with PB7 replaced by the timer 1 output while
    /// that is enabled by ACR bit 7.
    /// </summary>
    public byte PbOutput => (_acr & AcrT1Pb7) != 0
        ? (byte)((_orb & 0x7F) | (_t1Pb7 ? 0x80 : 0))
        : _orb;

    /// <summary>
    /// Data direction of PB0-PB7 (1 = output). PB7 is an output whenever
    /// the timer 1 output is enabled, regardless of DDRB.
    /// </summary>
    public byte PbDirection => (byte)((_acr & AcrT1Pb7) != 0 ? _ddrb | 0x80 : _ddrb);

    /// <summary>
    /// The level the chip is presenting on PB0-PB7 - see <see cref="PortA"/>.
    /// </summary>
    public byte PortB => (byte)((PB & ~PbDirection) | (PbOutput & PbDirection));

    // Control registers.

    private byte _acr;
    private byte _pcr;

    private int SrMode => (_acr >> 2) & 7;

    // Handshake lines.

    private bool Ca1ActiveHigh => (_pcr & 0x01) != 0;
    private bool Cb1ActiveHigh => (_pcr & 0x10) != 0;

    private int Ca2Mode => (_pcr >> 1) & 7;
    private int Cb2Mode => (_pcr >> 5) & 7;

    private bool Ca2IsOutput => (Ca2Mode & 4) != 0;
    private bool Cb2IsOutput => (Cb2Mode & 4) != 0 || SrMode >= 4;

    private bool Ca2HandshakeOrPulse => Ca2Mode is 4 or 5;
    private bool Ca2PulseMode => Ca2Mode == 5;
    private bool Cb2HandshakeOrPulse => Cb2Mode is 4 or 5 && SrMode < 4;
    private bool Cb2PulseMode => Cb2Mode == 5;

    private bool _ca1;
    private bool _ca2 = true;
    private bool _ca2Armed;
    private bool _ca2Restore;

    private bool _cb1In;
    private bool _cb1Out = true;
    private bool _cb2 = true;
    private bool _cb2In;
    private bool _cb2Armed;
    private bool _cb2Restore;

    /// <summary>
    /// Interrupt input CA1. An active transition (PCR bit 0) sets the CA1
    /// flag, latches port A when ACR bit 0 is set, and releases a CA2
    /// handshake output.
    /// </summary>
    public bool Ca1
    {
        get => _ca1;
        set
        {
            var rising = value && !_ca1;
            var falling = !value && _ca1;
            _ca1 = value;

            if (!(Ca1ActiveHigh ? rising : falling))
            {
                return;
            }

            if ((_acr & AcrLatchA) != 0)
            {
                _ira = PortA;
            }

            SetFlag(FlagCa1);

            if (Ca2Mode == 4)
            {
                _ca2 = true;
            }
        }
    }

    /// <summary>
    /// CA2: an input that sets the CA2 flag on an active transition when
    /// PCR bits 3-1 are 0xx, otherwise the level the chip drives (handshake,
    /// pulse, or a fixed low/high). Writes are ignored while it is an output.
    /// </summary>
    public bool Ca2
    {
        get => _ca2;
        set
        {
            if (Ca2IsOutput)
            {
                return;
            }

            var rising = value && !_ca2;
            var falling = !value && _ca2;
            _ca2 = value;

            // Modes 0/1 are negative edge, 2/3 positive edge.
            if ((Ca2Mode & 2) != 0 ? rising : falling)
            {
                SetFlag(FlagCa2);
            }
        }
    }

    /// <summary>
    /// Control line CB1: an input that sets the CB1 flag on an active
    /// transition (PCR bit 4) and latches port B when ACR bit 1 is set. It
    /// is the shift clock, and an output in the internally clocked shift
    /// modes, where writes are ignored.
    /// </summary>
    public bool Cb1
    {
        get => Cb1IsOutput ? _cb1Out : _cb1In;
        set
        {
            if (Cb1IsOutput)
            {
                return;
            }

            var rising = value && !_cb1In;
            var falling = !value && _cb1In;
            _cb1In = value;

            if (rising || falling)
            {
                if (Cb1ActiveHigh ? rising : falling)
                {
                    if ((_acr & AcrLatchB) != 0)
                    {
                        _irb = PortB;
                    }

                    SetFlag(FlagCb1);

                    if (Cb2Mode == 4 && SrMode < 4)
                    {
                        _cb2 = true;
                    }
                }

                if (SrMode is 3 or 7)
                {
                    ExternalShiftEdge(rising);
                }
            }
        }
    }

    private bool Cb1IsOutput => SrMode is 1 or 2 or 4 or 5 or 6;

    /// <summary>
    /// CB2: input (sets the CB2 flag like <see cref="Ca2"/>, and is the
    /// shift register's serial input), or the level the chip drives
    /// (handshake, pulse, fixed level, or serial output while shifting out).
    /// </summary>
    public bool Cb2
    {
        get => Cb2IsOutput ? _cb2 : _cb2In;
        set
        {
            if (Cb2IsOutput)
            {
                return;
            }

            var rising = value && !_cb2In;
            var falling = !value && _cb2In;
            _cb2In = value;

            if ((Cb2Mode & 2) != 0 ? rising : falling)
            {
                SetFlag(FlagCb2);
            }
        }
    }

    // Timer 1.

    private ushort _t1c;
    private byte _t1ll;
    private byte _t1lh;
    private bool _t1Armed;
    private bool _t1Reload;
    private bool _t1Pb7 = true;

    private void StepTimer1()
    {
        if (_t1Reload)
        {
            _t1Reload = false;
            _t1c = (ushort)((_t1lh << 8) | _t1ll);
            return;
        }

        _t1c--;

        if (_t1c != 0xFFFF)
        {
            return;
        }

        if (_t1Armed)
        {
            SetFlag(FlagTimer1);
            _t1Pb7 = !_t1Pb7;
        }

        // The counter reloads from the latches on every underflow, in
        // one-shot mode too; the mode only decides whether it flags again.
        _t1Reload = true;

        if ((_acr & AcrT1FreeRun) == 0)
        {
            _t1Armed = false;
        }
    }

    // Timer 2.

    private ushort _t2c;
    private byte _t2ll;
    private bool _t2Armed;
    private bool _t2Reload;

    private bool T2ClocksShifter => SrMode is 1 or 4 or 5;

    private void StepTimer2(bool pb6Falling)
    {
        if (T2ClocksShifter)
        {
            // The low byte counts and reloads from its latch on every
            // underflow, each of which clocks the shift register; the high
            // byte steps down once per underflow and is what finally flags.
            if (_t2Reload)
            {
                _t2Reload = false;
                _t2c = (ushort)((_t2c & 0xFF00) | _t2ll);
                return;
            }

            var low = (byte)_t2c;
            _t2c = (ushort)((_t2c & 0xFF00) | (byte)(low - 1));

            if (low != 0)
            {
                return;
            }

            _t2Reload = true;

            var high = (byte)(_t2c >> 8);

            if (high-- == 0 && _t2Armed)
            {
                SetFlag(FlagTimer2);
                _t2Armed = false;
            }

            _t2c = (ushort)((high << 8) | (_t2c & 0xFF));
            ClockShiftRegister();
            return;
        }

        if ((_acr & AcrT2PulseCount) != 0 && !pb6Falling)
        {
            return;
        }

        _t2c--;

        if (_t2c == 0xFFFF && _t2Armed)
        {
            SetFlag(FlagTimer2);
            _t2Armed = false;
        }
    }

    // Shift register.

    private byte _sr;
    private int _srRemaining;
    private bool _srActive;

    private void StartShifting()
    {
        ClearFlags(FlagShiftRegister);

        if (SrMode == 0)
        {
            return;
        }

        _srRemaining = 8;
        _srActive = true;
    }

    /// <summary>
    /// One transition of the internally generated shift clock on CB1.
    /// </summary>
    private void ClockShiftRegister()
    {
        if (!_srActive)
        {
            return;
        }

        if (_cb1Out)
        {
            _cb1Out = false;
            ShiftFallingEdge();
        }
        else
        {
            _cb1Out = true;
            ShiftRisingEdge();
        }
    }

    private void ExternalShiftEdge(bool rising)
    {
        if (!_srActive)
        {
            return;
        }

        if (rising)
        {
            ShiftRisingEdge();
        }
        else
        {
            ShiftFallingEdge();
        }
    }

    private void ShiftFallingEdge()
    {
        if (SrMode < 4)
        {
            return;
        }

        _cb2 = (_sr & 0x80) != 0;
        _sr = (byte)((_sr << 1) | (_sr >> 7));
    }

    private void ShiftRisingEdge()
    {
        if (SrMode < 4)
        {
            _sr = (byte)((_sr << 1) | (_cb2In ? 1 : 0));
        }

        if (SrMode == 4)
        {
            return;
        }

        if (--_srRemaining == 0)
        {
            _srActive = false;
            SetFlag(FlagShiftRegister);
        }
    }

    // Register access.

    private byte ReadRegister(int register)
    {
        switch (register)
        {
            case Orb:
            {
                ClearFlags((byte)(FlagCb1 | (Cb2Mode is 1 or 3 ? 0 : FlagCb2)));

                var pins = (_acr & AcrLatchB) != 0 ? _irb : PortB;
                var value = (byte)((_orb & _ddrb) | (pins & ~_ddrb));

                if ((_acr & AcrT1Pb7) != 0)
                {
                    value = (byte)((value & 0x7F) | (_t1Pb7 ? 0x80 : 0));
                }

                return value;
            }

            case Ora:
                ClearFlags((byte)(FlagCa1 | (Ca2Mode is 1 or 3 ? 0 : FlagCa2)));
                return (_acr & AcrLatchA) != 0 ? _ira : PortA;

            case OraNoHandshake:
                return (_acr & AcrLatchA) != 0 ? _ira : PortA;

            case Ddrb:
                return _ddrb;

            case Ddra:
                return _ddra;

            case T1Cl:
                ClearFlags(FlagTimer1);
                return (byte)_t1c;

            case T1Ch:
                return (byte)(_t1c >> 8);

            case T1Ll:
                return _t1ll;

            case T1Lh:
                return _t1lh;

            case T2Cl:
                ClearFlags(FlagTimer2);
                return (byte)_t2c;

            case T2Ch:
                return (byte)(_t2c >> 8);

            case Sr:
                return _sr;

            case AcrRegister:
                return _acr;

            case PcrRegister:
                return _pcr;

            case Ifr:
                return (byte)(_ifr | (Irq ? 0 : FlagAnyBit));

            default:
                return (byte)(_ier | FlagAnyBit);
        }
    }

    private void WriteRegister(int register, byte value)
    {
        switch (register)
        {
            case Orb:
                ClearFlags((byte)(FlagCb1 | (Cb2Mode is 1 or 3 ? 0 : FlagCb2)));
                _orb = value;
                break;

            case Ora:
                ClearFlags((byte)(FlagCa1 | (Ca2Mode is 1 or 3 ? 0 : FlagCa2)));
                _ora = value;
                break;

            case OraNoHandshake:
                _ora = value;
                break;

            case Ddrb:
                _ddrb = value;
                break;

            case Ddra:
                _ddra = value;
                break;

            case T1Cl:
            case T1Ll:
                _t1ll = value;
                break;

            case T1Lh:
                _t1lh = value;
                ClearFlags(FlagTimer1);
                break;

            case T1Ch:
                _t1lh = value;
                _t1c = (ushort)((value << 8) | _t1ll);
                _t1Armed = true;
                _t1Reload = false;
                _t1Pb7 = false;
                ClearFlags(FlagTimer1);
                break;

            case T2Cl:
                _t2ll = value;
                break;

            case T2Ch:
                _t2c = (ushort)((value << 8) | _t2ll);
                _t2Armed = true;
                ClearFlags(FlagTimer2);
                break;

            case Sr:
                _sr = value;
                break;

            case AcrRegister:
                _acr = value;
                break;

            case PcrRegister:
                _pcr = value;

                // Fixed-level outputs take effect at once.
                if (Ca2Mode == 6)
                {
                    _ca2 = false;
                }
                else if (Ca2Mode == 7)
                {
                    _ca2 = true;
                }

                if (SrMode < 4)
                {
                    if (Cb2Mode == 6)
                    {
                        _cb2 = false;
                    }
                    else if (Cb2Mode == 7)
                    {
                        _cb2 = true;
                    }
                }

                break;

            case Ifr:
                ClearFlags((byte)(value & 0x7F));
                break;

            case Ier:
                if ((value & FlagAnyBit) != 0)
                {
                    _ier |= (byte)(value & 0x7F);
                }
                else
                {
                    _ier &= (byte)~(value & 0x7F);
                }

                UpdateIrq();
                break;
        }
    }
}
