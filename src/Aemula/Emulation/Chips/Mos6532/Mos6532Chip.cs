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

    public Mos6532Pins Pins;

    public Mos6532Chip()
    {
        _ram = new byte[128];
        _timer = new Timer();
        Pins = new Mos6532Pins();
    }

    public void CpuCycle()
    {
        ref var pins = ref Pins;

        if (pins.Res)
        {
            _ddra = 0;
            _ddrb = 0;
            _ora = 0;
            _orb = 0;

            // TODO: Reset timer.

            pins.Res = false;
        }

        // Set IRQ pin based on interrupt flags.
        // The following condition tests whether one of the following are true:
        // - Timer interrupts are enabled, and the timer interrupt flag is set, or
        // - PA7 interrupts are enabled, and the PA7 interrupt flag is set.
        // Note that IRQ pin is active low.
        pins.Irq = (_irqState & _irqEnabled) == 0;

        if (pins.RS)
        {
            // Access I/O registers or interval timer.
            if ((pins.A & 0x4) != 0) // Check A2 pin
            {
                // Access interval timer.
                if (pins.RW)
                {
                    if ((pins.A & 0x1) != 0) // Check A0 pin
                    {
                        // Read interrupt flags.
                        pins.DB = _irqEnabled;
                        _irqState &= PA7FlagInverted; // Clear PA7 flag
                    }
                    else
                    {
                        // Read timer.
                        pins.DB = _timer.Value;
                        if (pins.DB != 0xFF)
                        {
                            _irqState &= TimerFlagInverted; // Clear timer flag
                        }
                        if ((pins.A & 0x8) != 0) // Check A3 pin
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
                    if ((pins.A & 0x10) != 0) // Check A4 pin
                    {
                        // Write timer.
                        var intervalDuration = GetIntervalDuration((byte)(pins.A & 0x3)); // A0 and A1 determine interval duration.
                        _timer.Reset(pins.DB, intervalDuration);
                        if (pins.DB != 0xFF)
                        {
                            _irqState &= TimerFlagInverted; // Clear timer flag
                        }
                        if ((pins.A & 0x8) != 0) // Check A3 pin
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
                        if ((pins.A & 0x2) != 0) // Check A1 pin
                        {
                            _irqEnabled |= PA7Flag;
                        }
                        else
                        {
                            _irqEnabled &= PA7FlagInverted;
                        }
                        _pa7ActiveEdgeDirection = (pins.A & 0x1) != 0; // Check A0 pin
                    }
                }
            }
            else
            {
                // Access I/O registers.
                var register = (byte)(pins.A & 0x3); // A0 and A1 determine register.
                if (pins.RW)
                {
                    // Read I/O registers.
                    pins.DB = ReadIORegister(register);
                }
                else
                {
                    // Write I/O registers.
                    WriteIORegister(register, pins.DB);
                }
            }
        }
        else
        {
            // Access RAM.
            if (pins.RW)
            {
                // Read RAM.
                pins.DB = _ram[pins.A];
            }
            else
            {
                // Write RAM.
                _ram[pins.A] = pins.DB;
            }
        }
    }

    public void Cycle()
    {
        // According to the diagram on page 2-57 of the R6532 data sheet,
        // the timer counts on the falling edge of phi2.
        _timer.Tick();

        // Either the timer has just expired, or the timer had already expired.
        if (_timer.Expired)
        {
            _irqState |= TimerFlag;
        }
    }

    private byte ReadIORegister(byte register)
    {
        return register switch
        {
            0b00 => (byte)(Pins.PA & ~_ddra | _ora & _ddra),
            0b01 => _ddra,
            0b10 => (byte)(Pins.PB & ~_ddrb | _orb & _ddrb),
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
