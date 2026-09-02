using System;

namespace Aemula.Emulation.Systems.Nes;

// The eight buttons of a standard NES pad, ordered as the controller shifts
// them out (A first, on bit 0).
[Flags]
public enum NesButton : byte
{
    None = 0,
    A = 1 << 0,
    B = 1 << 1,
    Select = 1 << 2,
    Start = 1 << 3,
    Up = 1 << 4,
    Down = 1 << 5,
    Left = 1 << 6,
    Right = 1 << 7,
}

// A standard NES controller: internally one CD4021B 8-stage parallel-in /
// serial-out shift register with the eight buttons wired to its parallel
// inputs. The console drives two lines into it - the latch (P/S control,
// pin 9, from the 2A03's OUT0) and the shift clock (pin 10, pulsed by the
// mainboard on each $4016/$4017 read) - and reads one bit back on the serial
// output (Q8, pin 3).
public sealed class NesController
{
    // The buttons currently held. The host key handler pokes this; while the
    // latch is high it feeds straight through to the parallel inputs.
    public NesButton Buttons { get; set; }

    private byte _shift;
    private bool _latch;
    private bool _clock;

    // P/S control. Transparent while high (the register tracks the live
    // buttons); the high->low edge freezes the snapshot to be shifted out.
    public bool Latch
    {
        get => _latch;
        set
        {
            if (!value)
            {
                _shift = (byte)Buttons;
            }
            _latch = value;
        }
    }

    // Shift clock. A low->high edge moves the register one place toward the
    // serial output, feeding a 1 into the vacated top bit - the pad's serial
    // input idles high, so the ninth read onward returns 1. Edges while the
    // latch is high are absorbed by the ongoing parallel load.
    public bool Clock
    {
        get => _clock;
        set
        {
            if (value && !_clock && !_latch)
            {
                _shift = (byte)((_shift >> 1) | 0x80);
            }
            _clock = value;
        }
    }

    // Serial output (Q8). While the latch is high this reflects the live A
    // button; otherwise it is the low bit of the frozen, shifting register.
    public bool SerialData => ((_latch ? (byte)Buttons : _shift) & 1) != 0;
}
