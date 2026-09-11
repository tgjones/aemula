using System.Collections.Generic;

namespace Aemula.Emulation.Systems.Atari2600;

// Host keyboard -> the left joystick (controller port 1): arrow keys for the
// four directions, space for the fire button. The stick directions are read
// as the top nibble of SWCHA (RIOT port A) and the button as INPT4 bit 7 on
// TIA. Every one of those lines is active-low with an idle pull-up on real
// hardware, so "not pressed" is a 1 and a press pulls the bit to 0.
public sealed partial class Atari2600System
{
    // SWCHA bit for each player 0 direction. The top nibble is player 0's
    // stick, ordered right/left/down/up from bit 7 down (Stella's M6532
    // assembles it as port0 pin one/two/three/four -> 0x10/0x20/0x40/0x80,
    // i.e. up/down/left/right); the low nibble is player 1's stick, which
    // nothing here drives.
    private const byte Joystick0Right = 0b1000_0000;
    private const byte Joystick0Left = 0b0100_0000;
    private const byte Joystick0Down = 0b0010_0000;
    private const byte Joystick0Up = 0b0001_0000;

    // TIA I-pin 4 is player 0's trigger; bit 7 of an INPT4 read follows it.
    private const byte Joystick0Fire = 0b0001_0000;

    // The generic InputScript tokens this system's OnKeyEvent understands,
    // each mapped to the Key it matches on below.
    public override IReadOnlyDictionary<string, Key> InputKeyBindings { get; } = new Dictionary<string, Key>
    {
        ["up"] = Key.Up,
        ["down"] = Key.Down,
        ["left"] = Key.Left,
        ["right"] = Key.Right,
        ["fire"] = Key.Space,
    };

    // Release every input line at power-on. Both ports and both triggers idle
    // high; without this the pins sit at their 0 default, which a game polling
    // SWCHA/INPT4 before it arms input latching would read as "everything
    // held down".
    private void InitializeInput()
    {
        _riot.PA = 0xFF;
        _tia.I |= 0b0011_0000;
    }

    public override void OnKeyEvent(KeyEvent keyEvent)
    {
        var direction = keyEvent.Key switch
        {
            Key.Up => Joystick0Up,
            Key.Down => Joystick0Down,
            Key.Left => Joystick0Left,
            Key.Right => Joystick0Right,
            _ => (byte)0,
        };

        if (direction != 0)
        {
            // Active-low: pressing pulls the bit to 0, releasing lets the
            // pull-up restore it.
            if (keyEvent.IsDown)
            {
                _riot.PA &= (byte)~direction;
            }
            else
            {
                _riot.PA |= direction;
            }
            return;
        }

        if (keyEvent.Key == Key.Space)
        {
            // Assigning I runs TIA's trigger-latch update, so a quick tap is
            // still caught when the game has INPT4/INPT5 latching enabled.
            if (keyEvent.IsDown)
            {
                _tia.I &= unchecked((byte)~Joystick0Fire);
            }
            else
            {
                _tia.I |= Joystick0Fire;
            }
        }
    }
}
