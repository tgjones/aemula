using System;

namespace Aemula.Emulation.Chips.Mos6532;

/// <summary>
/// 6532 chip, originally manufactured by MOS Technologies.
///
/// Known as RIOT (RAM, I/O, Timer), it contains:
/// - 128 bytes of RAM
/// - Two 8-bit bidirectional ports for communicating with peripherals
/// - Programmable interval timer
/// - Programmable edge detect circuit
/// </summary>
public sealed class Mos6532Chip
{
    private const byte TimerFlag = 0x80;
    private const byte PA7Flag = 0x40;

    private const byte TimerFlagInverted = unchecked((byte)~TimerFlag);
    private const byte PA7FlagInverted = unchecked((byte)~PA7Flag);

    // Pins

    private bool _res;

    /// <summary>
    /// Reset pin (input). Clears internal registers on the rising edge.
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

            // TODO: Reset timer.
        }
    }

    /// <summary>
    /// Read/write pin (input). Read = true, write = false.
    /// </summary>
    public bool RW { get; set; }

    /// <summary>
    /// Interrupt request pin (output). May be activated by either a transition on PA7,
    /// or timeout of the interval timer.
    /// </summary>
    public bool Irq { get; private set; }

    /// <summary>
    /// Data bus pins (D0-D7).
    /// </summary>
    public byte DB { get; set; }

    /// <summary>
    /// Address pins (A0-A6) (input).
    /// </summary>
    public byte A { get; set; }

    /// <summary>
    /// Peripheral A port pins (PA0-PA7).
    /// </summary>
    public byte PA { get; set; }

    /// <summary>
    /// Peripheral B port pins (PB0-PB7).
    /// </summary>
    public byte PB { get; set; }

    /// <summary>
    /// RAM Select pin (input).
    /// </summary>
    public bool RS { get; set; }

    private bool _cs1;
    /// <summary>
    /// Chip select 1 (input). Wired to A7 on the 2600 - active high.
    /// </summary>
    public bool CS1 { get => _cs1; set => _cs1 = value; }

    private bool _cs2;
    /// <summary>
    /// Chip select 2 (input). Wired to A12 on the 2600 - active low.
    /// </summary>
    public bool CS2 { get => _cs2; set => _cs2 = value; }

    /// <summary>
    /// Whether the chip-select pins currently indicate this RIOT is selected.
    /// </summary>
    private bool Selected => _cs1 && !_cs2;

    private bool _phi2;

    /// <summary>
    /// Clock input. Real RIOT has a single clock pin (unlike the 6502
    /// family's internal two-phase generator): the interval timer ticks on
    /// the falling edge, and a RAM/register access runs on the rising edge,
    /// gated on <see cref="Selected"/>.
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

            if (!value)
            {
                // According to the diagram on page 2-57 of the R6532 data sheet,
                // the timer counts on the falling edge of phi2.
                _timer.Tick();

                // Either the timer has just expired, or the timer had already expired.
                if (_timer.Expired)
                {
                    _irqState |= TimerFlag;
                }

                return;
            }

            if (!Selected)
            {
                return;
            }

            // Set IRQ pin based on interrupt flags.
            // The following condition tests whether one of the following are true:
            // - Timer interrupts are enabled, and the timer interrupt flag is set, or
            // - PA7 interrupts are enabled, and the PA7 interrupt flag is set.
            // Note that IRQ pin is active low.
            Irq = (_irqState & _irqEnabled) == 0;

            if (RS)
            {
                // Access I/O registers or interval timer.
                if ((A & 0x4) != 0) // Check A2 pin
                {
                    // Access interval timer.
                    if (RW)
                    {
                        if ((A & 0x1) != 0) // Check A0 pin
                        {
                            // Read interrupt flags.
                            DB = _irqEnabled;
                            _irqState &= PA7FlagInverted; // Clear PA7 flag
                        }
                        else
                        {
                            // Read timer.
                            DB = _timer.Value;
                            if (DB != 0xFF)
                            {
                                _irqState &= TimerFlagInverted; // Clear timer flag
                            }
                            if ((A & 0x8) != 0) // Check A3 pin
                            {
                                _irqEnabled |= TimerFlag;
                            }
                            else
                            {
                                _irqEnabled &= TimerFlagInverted;
                            }
                        }
                    }
                    else
                    {
                        if ((A & 0x10) != 0) // Check A4 pin
                        {
                            // Write timer.
                            var intervalDuration = GetIntervalDuration((byte)(A & 0x3)); // A0 and A1 determine interval duration.
                            _timer.Reset(DB, intervalDuration);
                            if (DB != 0xFF)
                            {
                                _irqState &= TimerFlagInverted; // Clear timer flag
                            }
                            if ((A & 0x8) != 0) // Check A3 pin
                            {
                                _irqEnabled |= TimerFlag;
                            }
                            else
                            {
                                _irqEnabled &= TimerFlagInverted;
                            }
                        }
                        else
                        {
                            // Write edge detect control.
                            if ((A & 0x2) != 0) // Check A1 pin
                            {
                                _irqEnabled |= PA7Flag;
                            }
                            else
                            {
                                _irqEnabled &= PA7FlagInverted;
                            }
                            _pa7ActiveEdgeDirection = (A & 0x1) != 0; // Check A0 pin
                        }
                    }
                }
                else
                {
                    // Access I/O registers.
                    var register = (byte)(A & 0x3); // A0 and A1 determine register.
                    if (RW)
                    {
                        // Read I/O registers.
                        DB = ReadIORegister(register);
                    }
                    else
                    {
                        // Write I/O registers.
                        WriteIORegister(register, DB);
                    }
                }
            }
            else
            {
                // Access RAM.
                if (RW)
                {
                    // Read RAM.
                    DB = _ram[A];
                }
                else
                {
                    // Write RAM.
                    _ram[A] = DB;
                }
            }
        }
    }

    // Internal state

    /// <summary>
    /// 128 bytes of RAM.
    /// </summary>
    private readonly byte[] _ram;

    /// <summary>
    /// Data direction register A.
    /// </summary>
    private byte _ddra;

    /// <summary>
    /// Data direction register B.
    /// </summary>
    private byte _ddrb;

    /// <summary>
    /// Output register A.
    /// </summary>
    private byte _ora;

    /// <summary>
    /// Output register B.
    /// </summary>
    private byte _orb;

    /// <summary>
    /// Handles the timer part of the RIOT chip.
    /// </summary>
    private Timer _timer;

    /// <summary>
    /// Stores whether timer and PA7 interrupts are enabled.
    /// Bit 7 is 1 if timer interrupts are enabled.
    /// Bit 6 is 1 if PA7 interrupts are enabled.
    /// </summary>
    private byte _irqEnabled;

    /// <summary>
    /// Current state of the two interrupt flags: timer and PA7.
    /// Bit 7 is 1 if a timer interrupt should occur.
    /// Bit 6 is 1 if a PA7 interrupt should occur.
    /// If either of these is set to 1, the IRQ pin will be set low.
    /// </summary>
    private byte _irqState;

    /// <summary>
    /// True for positive edge-detect, false for negative edge-detect.
    /// </summary>
    private bool _pa7ActiveEdgeDirection;

    public Mos6532Chip()
    {
        _ram = new byte[128];
        _timer = new Timer();
    }

    private byte ReadIORegister(byte register)
    {
        return register switch
        {
            0b00 => (byte)(PA & ~_ddra | _ora & _ddra),
            0b01 => _ddra,
            0b10 => (byte)(PB & ~_ddrb | _orb & _ddrb),
            0b11 => _ddrb,
            _ => throw new InvalidOperationException()
        };
    }

    private void WriteIORegister(byte register, byte data)
    {
        switch (register)
        {
            case 0b00: _ora = data; break;
            case 0b01: _ddra = data; break;
            case 0b10: _orb = data; break;
            case 0b11: _ddrb = data; break;
            default: throw new InvalidOperationException();
        }
    }

    public byte ReadByteDebug(ushort address) => _ram[address];

    private static ushort GetIntervalDuration(byte a1a0)
    {
        return a1a0 switch
        {
            0b00 => 1,
            0b01 => 8,
            0b10 => 64,
            0b11 => 1024,
            _ => throw new InvalidOperationException()
        };
    }
}
