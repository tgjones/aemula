using Hexa.NET.SDL3;

namespace Aemula.Emulation.Systems.Atari2600;

// Host keyboard -> the left joystick (controller port 1): arrow keys for the
// four directions, space for the fire button. The stick directions are read
// as the top nibble of SWCHA (RIOT port A) and the button as INPT4 bit 7 on
// TIA. Every one of those lines is active-low with an idle pull-up on real
// hardware, so "not pressed" is a 1 and a press pulls the bit to 0.
public sealed partial class Atari2600System
{
    // SWCHA bit for each player 0 direction (Stella Programmer's Guide, the
    // SWCHA read). The low nibble is player 1's stick, which nothing here
    // drives.
    private const byte Joystick0Up = 0b1000_0000;
    private const byte Joystick0Down = 0b0100_0000;
    private const byte Joystick0Left = 0b0010_0000;
    private const byte Joystick0Right = 0b0001_0000;

    // TIA I-pin 4 is player 0's trigger; bit 7 of an INPT4 read follows it.
    private const byte Joystick0Fire = 0b0001_0000;

    // SDL reports the arrow keys as scancode-derived keycodes:
    // SDLK_* == SDL_SCANCODE_MASK (1 << 30) | scancode. Spelt out as literals
    // to match the rest of the codebase (see SpaceInvadersSystem.OnKeyEvent).
    private const int SdlkRight = 0x4000004F;
    private const int SdlkLeft = 0x40000050;
    private const int SdlkDown = 0x40000051;
    private const int SdlkUp = 0x40000052;
    private const int SdlkSpace = 0x20;

    // Release every input line at power-on. Both ports and both triggers idle
    // high; without this the pins sit at their 0 default, which a game polling
    // SWCHA/INPT4 before it arms input latching would read as "everything
    // held down".
    private void InitializeInput()
    {
        _riot.PA = 0xFF;
        _tia.I |= 0b0011_0000;
    }

    public override void OnKeyEvent(SDLKeyboardEvent keyEvent)
    {
        var isKeyDown = keyEvent.Type == SDLEventType.KeyDown;

        var direction = keyEvent.Key switch
        {
            SdlkUp => Joystick0Up,
            SdlkDown => Joystick0Down,
            SdlkLeft => Joystick0Left,
            SdlkRight => Joystick0Right,
            _ => (byte)0,
        };

        if (direction != 0)
        {
            // Active-low: pressing pulls the bit to 0, releasing lets the
            // pull-up restore it.
            if (isKeyDown)
            {
                _riot.PA &= (byte)~direction;
            }
            else
            {
                _riot.PA |= direction;
            }
            return;
        }

        if (keyEvent.Key == SdlkSpace)
        {
            // Assigning I runs TIA's trigger-latch update, so a quick tap is
            // still caught when the game has INPT4/INPT5 latching enabled.
            if (isKeyDown)
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
