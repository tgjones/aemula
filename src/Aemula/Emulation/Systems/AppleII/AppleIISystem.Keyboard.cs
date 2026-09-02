using Aemula.Emulation.Chips;
using Hexa.NET.SDL3;

namespace Aemula.Emulation.Systems.AppleII;

// The AY-5-3600 keyboard matrix encoder and the $C000/$C010 soft
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

    // The host key currently driving a matrix crosspoint, if any. Tracked by
    // its layout-dependent base keycode (which doesn't change as modifiers go
    // up and down), so a key-up releases the right crosspoint even if Shift
    // was let go first.
    private int? _heldKey;

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

    // Modifiers that select a keyboard layout's shift level (plain Shift, plus
    // AltGr / Option, however the host reports it). Ctrl and Gui/Cmd are
    // deliberately excluded: Ctrl drives the encoder's own Control line, and
    // Gui/Cmd chords aren't for the emulated machine at all.
    private const SDLKeymod LayoutShiftMods =
        SDLKeymod.Shift | SDLKeymod.Alt | SDLKeymod.Mode;

    public override void OnKeyEvent(SDLKeyboardEvent keyEvent)
    {
        var isKeyDown = keyEvent.Type == SDLEventType.KeyDown;
        var mod = (SDLKeymod)keyEvent.Mod;

        // Cmd/Win chords belong to the host, never the emulated keyboard.
        // (Ctrl is left alone here - the II+ uses it for its control codes.)
        if ((mod & SDLKeymod.Gui) != 0)
        {
            return;
        }

        // Resolve the character this key actually produces in the host's
        // current keyboard layout, with Shift/AltGr applied (Ctrl masked out -
        // it's handled separately below). The final arg must be false: a
        // key_event keycode is deliberately modifier-independent (it's what
        // lands in keyEvent.Key), whereas false means "translate this scancode
        // under the given modifier state" - i.e. actually apply Shift.
        // GetKeyFromScancode needs a real scancode and an initialised video
        // subsystem; synthetic events (unit tests) carry neither, so fall back
        // to the raw keycode.
        var character = keyEvent.Scancode != SDLScancode.Unknown
            ? SDL.GetKeyFromScancode(keyEvent.Scancode, (ushort)(mod & LayoutShiftMods), false)
            : keyEvent.Key;

        var position = MapCharToMatrixPosition(character);
        if (position is null)
        {
            return;
        }

        if (isKeyDown)
        {
            var (x, y, appleShift) = position.Value;
            _heldKey = keyEvent.Key;
            _keyboardEncoder.Shift = appleShift;
            _keyboardEncoder.Control = (mod & SDLKeymod.Ctrl) != 0;
            _keyboardEncoder.SetPressedKey(x, y);
        }
        else if (_heldKey == keyEvent.Key)
        {
            _heldKey = null;
            _keyboardEncoder.SetPressedKey(-1, -1);
        }
    }

    // Maps a resolved character to the Apple II+ matrix crosspoint (Sather
    // ch. 7 / "The Apple II Circuit Description") and the Shift state the II+'s
    // own encoder ROM needs to emit that character. Driving the mapping by the
    // produced character rather than the physical key means the PC keycap
    // legend is what you get - PC Shift-2 types '"', Shift-6 types '^' - and
    // it follows whatever layout macOS has active, since the character was
    // resolved through that layout. Characters the II+ keyboard can't produce
    // (lowercase is fine - the ROM folds it; but '[' '\' '_' '{' '~' etc.)
    // map to nothing. Backspace/Delete drive the left-arrow key, which the
    // monitor and Applesoft treat as destructive backspace.
    internal static (int X, int Y, bool Shift)? MapCharToMatrixPosition(int character) => character switch
    {
        '0' => (0, 7, false),
        '1' => (4, 2, false),
        '2' => (4, 1, false),
        '3' => (0, 0, false),
        '4' => (0, 1, false),
        '5' => (0, 2, false),
        '6' => (0, 3, false),
        '7' => (0, 4, false),
        '8' => (0, 5, false),
        '9' => (0, 6, false),

        '!' => (4, 2, true),
        '"' => (4, 1, true),
        '#' => (0, 0, true),
        '$' => (0, 1, true),
        '%' => (0, 2, true),
        '&' => (0, 3, true),
        '\'' => (0, 4, true),
        '(' => (0, 5, true),
        ')' => (0, 6, true),

        ':' => (0, 8, false),
        '*' => (0, 8, true),
        ';' => (2, 7, false),
        '+' => (2, 7, true),
        '-' => (0, 9, false),
        '=' => (0, 9, true),
        ',' => (3, 7, false),
        '<' => (3, 7, true),
        '.' => (3, 8, false),
        '>' => (3, 8, true),
        '/' => (3, 9, false),
        '?' => (3, 9, true),
        '@' => (1, 9, true),
        '^' => (3, 5, true),
        ']' => (3, 6, true),

        'a' or 'A' => (4, 4, false),
        'b' or 'B' => (3, 4, false),
        'c' or 'C' => (3, 2, false),
        'd' or 'D' => (2, 0, false),
        'e' or 'E' => (1, 2, false),
        'f' or 'F' => (2, 1, false),
        'g' or 'G' => (2, 2, false),
        'h' or 'H' => (2, 3, false),
        'i' or 'I' => (1, 7, false),
        'j' or 'J' => (2, 4, false),
        'k' or 'K' => (2, 5, false),
        'l' or 'L' => (2, 6, false),
        'm' or 'M' => (3, 6, false),
        'n' or 'N' => (3, 5, false),
        'o' or 'O' => (1, 8, false),
        'p' or 'P' => (1, 9, false),
        'q' or 'Q' => (1, 0, false),
        'r' or 'R' => (1, 3, false),
        's' or 'S' => (4, 0, false),
        't' or 'T' => (1, 4, false),
        'u' or 'U' => (1, 6, false),
        'v' or 'V' => (3, 3, false),
        'w' or 'W' => (1, 1, false),
        'x' or 'X' => (3, 1, false),
        'y' or 'Y' => (1, 5, false),
        'z' or 'Z' => (3, 0, false),

        ' ' => (4, 5, false),          // Space.
        0x0D or 0x0A => (4, 9, false), // Return.
        0x1B => (4, 3, false),         // Escape.
        0x08 or 0x7F => (2, 8, false), // Backspace / Delete -> left arrow.
        0x40000050 => (2, 8, false),   // SDLK_LEFT.
        0x4000004F => (2, 9, false),   // SDLK_RIGHT.

        _ => null,
    };
}
