using Aemula.Emulation.Chips;
using Hexa.NET.SDL3;

namespace Aemula.Emulation.Systems.AppleII;

// Phase 4: the AY-5-3600 keyboard matrix encoder and the $C000/$C010 soft
// switches. The Apple II+'s ~47-key matrix wiring and the $C000/$C010
// latch/clear behaviour are drawn from Jim Sather's "Understanding the
// Apple II" chapter 7 and "The Apple II Circuit Description"; see
// Ay53600Chip for the encoder itself.
public sealed partial class AppleIISystem
{
    private readonly Ay53600Chip _keyboardEncoder;

    // The motherboard flip-flop the AY-5-3600's strobe output sets (data
    // bit 7 at $C000); cleared by any access to $C010-$C01F.
    private readonly Ttl7474Chip _keyboardStrobeLatch;

    // Divides the master clock down to roughly the AY-5-3600's internal
    // ~80KHz RC oscillator rate - it's a free-running clock on real
    // hardware, asynchronous to everything else on the board.
    private const int KeyboardScanDivider = 179;
    private uint _keyboardScanDividerCounter;

    private bool _leftShiftHeld;
    private bool _rightShiftHeld;
    private bool _leftControlHeld;
    private bool _rightControlHeld;
    private (int X, int Y)? _heldKeyPosition;

    private void TickKeyboard()
    {
        _keyboardScanDividerCounter++;

        if (_keyboardScanDividerCounter < KeyboardScanDivider)
        {
            return;
        }

        _keyboardScanDividerCounter = 0;

        _keyboardEncoder.Tick();

        if (_keyboardEncoder.DataReady)
        {
            _keyboardStrobeLatch.Pre1 = false;
            _keyboardStrobeLatch.Pre1 = true;
        }
    }

    private byte ReadKeyboardData()
    {
        var value = (byte)(_keyboardEncoder.Data & 0x7F);

        if (_keyboardStrobeLatch.Q1)
        {
            value |= 0x80;
        }

        return value;
    }

    private void ClearKeyboardStrobe()
    {
        _keyboardStrobeLatch.Clr1 = false;
        _keyboardStrobeLatch.Clr1 = true;
    }

    public override void OnKeyEvent(SDLKeyboardEvent keyEvent)
    {
        var isKeyDown = keyEvent.Type == SDLEventType.KeyDown;

        switch (keyEvent.Key)
        {
            case 0x400000e0: // SDLK_LCTRL
                _leftControlHeld = isKeyDown;
                _keyboardEncoder.Control = _leftControlHeld || _rightControlHeld;
                return;

            case 0x400000e4: // SDLK_RCTRL
                _rightControlHeld = isKeyDown;
                _keyboardEncoder.Control = _leftControlHeld || _rightControlHeld;
                return;

            case 0x400000e1: // SDLK_LSHIFT
                _leftShiftHeld = isKeyDown;
                _keyboardEncoder.Shift = _leftShiftHeld || _rightShiftHeld;
                return;

            case 0x400000e5: // SDLK_RSHIFT
                _rightShiftHeld = isKeyDown;
                _keyboardEncoder.Shift = _leftShiftHeld || _rightShiftHeld;
                return;
        }

        var position = MapKeyToMatrixPosition(keyEvent.Key);
        if (position is null)
        {
            return;
        }

        if (isKeyDown)
        {
            _heldKeyPosition = position;
            _keyboardEncoder.SetPressedKey(position.Value.X, position.Value.Y);
        }
        else if (_heldKeyPosition == position)
        {
            _heldKeyPosition = null;
            _keyboardEncoder.SetPressedKey(-1, -1);
        }
    }

    // The Apple II+'s 47-key matrix (Sather ch. 7 / "The Apple II Circuit
    // Description"), mapped to a PC keyboard's closest equivalent key. Two
    // Apple keys with no direct PC equivalent (the dedicated ":"/"*" and
    // ";"/"+" keys) are mapped to the PC's ";" and "=" keys respectively.
    private static (int X, int Y)? MapKeyToMatrixPosition(int key) => key switch
    {
        (int)'3' => (0, 0),
        (int)'4' => (0, 1),
        (int)'5' => (0, 2),
        (int)'6' => (0, 3),
        (int)'7' => (0, 4),
        (int)'8' => (0, 5),
        (int)'9' => (0, 6),
        (int)'0' => (0, 7),
        (int)';' => (0, 8),
        (int)'-' => (0, 9),

        (int)'q' => (1, 0),
        (int)'w' => (1, 1),
        (int)'e' => (1, 2),
        (int)'r' => (1, 3),
        (int)'t' => (1, 4),
        (int)'y' => (1, 5),
        (int)'u' => (1, 6),
        (int)'i' => (1, 7),
        (int)'o' => (1, 8),
        (int)'p' => (1, 9),

        (int)'d' => (2, 0),
        (int)'f' => (2, 1),
        (int)'g' => (2, 2),
        (int)'h' => (2, 3),
        (int)'j' => (2, 4),
        (int)'k' => (2, 5),
        (int)'l' => (2, 6),
        (int)'=' => (2, 7),
        0x40000050 => (2, 8), // SDLK_LEFT
        0x4000004f => (2, 9), // SDLK_RIGHT

        (int)'z' => (3, 0),
        (int)'x' => (3, 1),
        (int)'c' => (3, 2),
        (int)'v' => (3, 3),
        (int)'b' => (3, 4),
        (int)'n' => (3, 5),
        (int)'m' => (3, 6),
        (int)',' => (3, 7),
        (int)'.' => (3, 8),
        (int)'/' => (3, 9),

        (int)'s' => (4, 0),
        (int)'2' => (4, 1),
        (int)'1' => (4, 2),
        0x1B => (4, 3), // SDLK_ESCAPE
        (int)'a' => (4, 4),
        (int)' ' => (4, 5), // SDLK_SPACE
        0x0D => (4, 9), // SDLK_RETURN

        _ => null,
    };
}
