namespace Aemula.Emulation.Chips.Mos6820;

/// <summary>
/// 6820 chip, originally manufactured by Motorola.
///
/// Known as the PIA (Peripheral Interface Adapter), it provides two 8-bit
/// bidirectional peripheral ports (A and B), each with its own data
/// direction register and a pair of handshake control lines (CA1/CA2,
/// CB1/CB2), addressed by the MPU through a 6800-style bus (register
/// selects, R/W, an E clock, and a reset line).
///
/// Register addressing: <see cref="Rs1"/> picks side A (low) or B (high);
/// with <see cref="Rs0"/> low, bit 2 of that side's control register then
/// picks the data direction register (0) or the peripheral (output)
/// register (1); with <see cref="Rs0"/> high, the control register itself is
/// addressed regardless of that bit. Software can only write bits 0-5 of a
/// control register - bits 6 and 7 are the CA1/CA2 (or CB1/CB2) interrupt
/// flags, set only by an active transition on the corresponding line and
/// cleared only by an MPU read of that side's peripheral register.
///
/// CA2/CB2 in output mode is the trickiest part of the real chip and the
/// reason this class exists rather than a plain register file: bits 5,4,3
/// of the control register select between handshake mode (the line pulses
/// low around a peripheral-register access and is restored by the paired
/// CA1/CB1 active transition), pulse mode (same low pulse, but restored
/// automatically one E cycle later instead of waiting on CA1/CB1), and two
/// manual modes that just force the line low or high.
/// </summary>
public sealed class Mos6820Chip
{
    private const byte Irq1FlagBit = 0x80;
    private const byte Irq2FlagBit = 0x40;
    private const byte C2DirectionBit = 0x20;
    private const byte C2EdgeOrSubmodeHighBit = 0x10;
    private const byte Irq2EnableOrSubmodeLowBit = 0x08;
    private const byte DdrAccessBit = 0x04;
    private const byte C1EdgeBit = 0x02;
    private const byte Irq1EnableBit = 0x01;

    private const byte FlagBitsMask = Irq1FlagBit | Irq2FlagBit;
    private const byte ControlWritableMask = 0x3F;

    // Bus pins.

    public byte DB { get; set; }

    public bool Rs0 { private get; set; }
    public bool Rs1 { private get; set; }

    /// <summary>
    /// Read/write pin (input). Read = true, write = false.
    /// </summary>
    public bool RW { private get; set; }

    public bool Cs0 { private get; set; }
    public bool Cs1 { private get; set; }

    /// <summary>
    /// Chip select 2, active low.
    /// </summary>
    public bool Cs2 { private get; set; }

    private bool Selected => Cs0 && Cs1 && !Cs2;

    /// <summary>
    /// Interrupt request, side A (output). Active low, open-source on real
    /// hardware so several PIAs can wire-OR onto one MPU interrupt line.
    /// </summary>
    public bool Irqa { get; private set; } = true;

    /// <summary>
    /// Interrupt request, side B (output).
    /// </summary>
    public bool Irqb { get; private set; } = true;

    private bool _res;

    /// <summary>
    /// Reset pin (input), active low. All registers clear on the rising
    /// edge (i.e. once reset is released), matching <see cref="Mos6532.Mos6532Chip.Res"/>.
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

            _ddra = 0;
            _ddrb = 0;
            _ora = 0;
            _orb = 0;
            _cra = 0;
            _crb = 0;
            _ca2 = true;
            _cb2 = true;
            _ca2StrobeArmed = false;
            _ca2RestoreArmed = false;
            _cb2StrobeArmed = false;
            _cb2RestoreArmed = false;
            UpdateIrqA();
            UpdateIrqB();
        }
    }

    private bool _e;

    /// <summary>
    /// Enable clock (input) - the only timing signal the MPU supplies. A
    /// selected read is presented on <see cref="DB"/> combinationally once
    /// <see cref="E"/> goes high; a selected write latches <see cref="DB"/>
    /// on the falling edge. CA2/CB2 output-mode pulses are timed relative to
    /// these same edges (see the class remarks).
    /// </summary>
    public bool E
    {
        get => _e;
        set
        {
            if (_e == value)
            {
                return;
            }

            _e = value;

            if (value)
            {
                if (Selected && RW)
                {
                    var readingOra = !Rs0 && !Rs1 && (_cra & DdrAccessBit) != 0;

                    DB = ReadRegister();

                    if (readingOra && Ca2HandshakeOrPulse)
                    {
                        _ca2StrobeArmed = true;
                    }
                }

                if (_cb2StrobeArmed)
                {
                    _cb2 = false;
                    _cb2StrobeArmed = false;

                    if (Cb2PulseMode)
                    {
                        _cb2RestoreArmed = true;
                    }
                }
                else if (_cb2RestoreArmed)
                {
                    _cb2 = true;
                    _cb2RestoreArmed = false;
                }
            }
            else
            {
                if (Selected && !RW)
                {
                    var writingOrb = !Rs0 && Rs1 && (_crb & DdrAccessBit) != 0;

                    WriteRegister(DB);

                    if (writingOrb && Cb2HandshakeOrPulse)
                    {
                        _cb2StrobeArmed = true;
                    }
                }

                if (_ca2StrobeArmed)
                {
                    _ca2 = false;
                    _ca2StrobeArmed = false;

                    if (Ca2PulseMode)
                    {
                        _ca2RestoreArmed = true;
                    }
                }
                else if (_ca2RestoreArmed)
                {
                    _ca2 = true;
                    _ca2RestoreArmed = false;
                }
            }
        }
    }

    // Peripheral side A.

    /// <summary>
    /// Peripheral A port pins (input) - the level externally driven onto
    /// PA0-PA7 by whatever's connected, used for bits <see cref="_ddra"/>
    /// marks as inputs.
    /// </summary>
    public byte PA { private get; set; }

    private byte _ddra;
    private byte _ora;

    /// <summary>
    /// The level the chip is presenting on PA0-PA7: <see cref="PA"/> passed
    /// through on input-configured bits, the output register on
    /// output-configured bits. Side effect free, unlike an MPU register
    /// read - safe for the system wiring code to sample every tick.
    /// </summary>
    public byte PortA => (byte)((PA & ~_ddra) | (_ora & _ddra));

    private byte _cra;

    private bool Ca2IsOutput => (_cra & C2DirectionBit) != 0;

    // 0 = handshake, 1 = pulse, 2 = always low, 3 = always high - bits 4
    // and 3 read as a little-endian 2-bit index into that list.
    private int Ca2Submode => (_cra >> 3) & 0x03;

    private bool Ca2HandshakeOrPulse => Ca2IsOutput && Ca2Submode <= 1;
    private bool Ca2PulseMode => Ca2IsOutput && Ca2Submode == 1;

    private bool _ca1;

    /// <summary>
    /// Interrupt input, side A (input only on real hardware). An active
    /// transition (selected by control-register bit 1) always sets the
    /// IRQA1 flag; whether that also pulls <see cref="Irqa"/> low depends on
    /// the IRQ1 enable bit. In CA2 handshake-output mode this is also what
    /// restores <see cref="Ca2"/> high.
    /// </summary>
    public bool Ca1
    {
        set
        {
            var risingEdge = value && !_ca1;
            var fallingEdge = !value && _ca1;
            _ca1 = value;

            var activeEdge = (_cra & C1EdgeBit) != 0 ? risingEdge : fallingEdge;

            if (!activeEdge)
            {
                return;
            }

            _cra |= Irq1FlagBit;
            UpdateIrqA();

            if (Ca2IsOutput && Ca2Submode == 0)
            {
                _ca2 = true;
            }
        }
    }

    private bool _ca2 = true;
    private bool _ca2StrobeArmed;
    private bool _ca2RestoreArmed;

    /// <summary>
    /// Peripheral control line CA2 - bidirectional, per control-register
    /// bit 5. As an input, an external device drives this and an active
    /// transition (bit 4 selects which) sets the IRQA2 flag, same shape as
    /// <see cref="Ca1"/>. As an output, external writes are ignored and this
    /// instead reflects whatever the chip itself is driving (see the class
    /// remarks for the four output submodes).
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

            var risingEdge = value && !_ca2;
            var fallingEdge = !value && _ca2;
            _ca2 = value;

            var activeEdge = (_cra & C2EdgeOrSubmodeHighBit) != 0 ? risingEdge : fallingEdge;

            if (!activeEdge)
            {
                return;
            }

            _cra |= Irq2FlagBit;
            UpdateIrqA();
        }
    }

    private void UpdateIrqA()
    {
        Irqa = !(((_cra & Irq1FlagBit) != 0 && (_cra & Irq1EnableBit) != 0)
            || ((_cra & Irq2FlagBit) != 0 && (_cra & Irq2EnableOrSubmodeLowBit) != 0));
    }

    private void ApplyCa2ManualLevel()
    {
        if (!Ca2IsOutput)
        {
            return;
        }

        switch (Ca2Submode)
        {
            case 2:
                _ca2 = false;
                break;
            case 3:
                _ca2 = true;
                break;
        }
    }

    // Peripheral side B.

    /// <summary>
    /// Peripheral B port pins (input) - see <see cref="PA"/>.
    /// </summary>
    public byte PB { private get; set; }

    private byte _ddrb;
    private byte _orb;

    /// <summary>
    /// The level the chip is presenting on PB0-PB7 - see <see cref="PortA"/>.
    /// </summary>
    public byte PortB => (byte)((PB & ~_ddrb) | (_orb & _ddrb));

    private byte _crb;

    private bool Cb2IsOutput => (_crb & C2DirectionBit) != 0;
    private int Cb2Submode => (_crb >> 3) & 0x03;
    private bool Cb2HandshakeOrPulse => Cb2IsOutput && Cb2Submode <= 1;
    private bool Cb2PulseMode => Cb2IsOutput && Cb2Submode == 1;

    private bool _cb1;

    /// <summary>
    /// Interrupt input, side B - see <see cref="Ca1"/>.
    /// </summary>
    public bool Cb1
    {
        set
        {
            var risingEdge = value && !_cb1;
            var fallingEdge = !value && _cb1;
            _cb1 = value;

            var activeEdge = (_crb & C1EdgeBit) != 0 ? risingEdge : fallingEdge;

            if (!activeEdge)
            {
                return;
            }

            _crb |= Irq1FlagBit;
            UpdateIrqB();

            if (Cb2IsOutput && Cb2Submode == 0)
            {
                _cb2 = true;
            }
        }
    }

    private bool _cb2 = true;
    private bool _cb2StrobeArmed;
    private bool _cb2RestoreArmed;

    /// <summary>
    /// Peripheral control line CB2 - see <see cref="Ca2"/>. The output-mode
    /// handshake/pulse here is triggered by an MPU write to the peripheral
    /// register (rather than a read, as on CA2) and lands on E's rising
    /// edge (rather than falling) - see the class remarks.
    /// </summary>
    public bool Cb2
    {
        get => _cb2;
        set
        {
            if (Cb2IsOutput)
            {
                return;
            }

            var risingEdge = value && !_cb2;
            var fallingEdge = !value && _cb2;
            _cb2 = value;

            var activeEdge = (_crb & C2EdgeOrSubmodeHighBit) != 0 ? risingEdge : fallingEdge;

            if (!activeEdge)
            {
                return;
            }

            _crb |= Irq2FlagBit;
            UpdateIrqB();
        }
    }

    private void UpdateIrqB()
    {
        Irqb = !(((_crb & Irq1FlagBit) != 0 && (_crb & Irq1EnableBit) != 0)
            || ((_crb & Irq2FlagBit) != 0 && (_crb & Irq2EnableOrSubmodeLowBit) != 0));
    }

    private void ApplyCb2ManualLevel()
    {
        if (!Cb2IsOutput)
        {
            return;
        }

        switch (Cb2Submode)
        {
            case 2:
                _cb2 = false;
                break;
            case 3:
                _cb2 = true;
                break;
        }
    }

    // Register access.

    private byte ReadRegister()
    {
        if (Rs0)
        {
            return Rs1 ? _crb : _cra;
        }

        if (!Rs1)
        {
            if ((_cra & DdrAccessBit) != 0)
            {
                _cra &= FlagBitsMask ^ 0xFF;
                UpdateIrqA();
                return PortA;
            }

            return _ddra;
        }

        if ((_crb & DdrAccessBit) != 0)
        {
            _crb &= FlagBitsMask ^ 0xFF;
            UpdateIrqB();
            return PortB;
        }

        return _ddrb;
    }

    private void WriteRegister(byte value)
    {
        if (Rs0)
        {
            if (Rs1)
            {
                _crb = (byte)((_crb & FlagBitsMask) | (value & ControlWritableMask));
                ApplyCb2ManualLevel();
            }
            else
            {
                _cra = (byte)((_cra & FlagBitsMask) | (value & ControlWritableMask));
                ApplyCa2ManualLevel();
            }

            return;
        }

        if (!Rs1)
        {
            if ((_cra & DdrAccessBit) != 0)
            {
                _ora = value;
            }
            else
            {
                _ddra = value;
            }

            return;
        }

        if ((_crb & DdrAccessBit) != 0)
        {
            _orb = value;
        }
        else
        {
            _ddrb = value;
        }
    }
}
