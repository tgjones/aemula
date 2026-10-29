using System;
using System.Collections.Generic;

namespace Aemula.Emulation.Systems.BbcMicro;

// The keyboard is a separate board on PL13 with its own logic, so (as with
// the ZX80's matrix) it is modelled by its documented behaviour rather than
// wired. It sees System VIA port A and the addressable latch's Q3:
//
//  - Q3 low: the keyboard is read by hand. PA0-PA3 select a column (0-9) and
//    PA4-PA6 a row (0-7); the keyboard drives PA7 high if the key at that
//    crosspoint is down, and CA2 high if any key in the selected column on
//    rows 1-7 is down.
//  - Q3 high: the keyboard scans its columns by itself, and CA2 is high while
//    any key on rows 1-7 anywhere is down, which is what interrupts the OS to
//    start a scan. Row 0 (SHIFT, CTRL and the start-up links) is left out of
//    CA2, so holding SHIFT alone raises no interrupt.
public sealed partial class BbcMicroSystem
{
    private const int KeyboardColumns = 10;

    // Bit N of a column's byte is set while the key on row N of that column
    // is down. Rebuilt from _heldKeys on every key event, since SHIFT and
    // CTRL are shared by every held key that needs them.
    private readonly byte[] _keyColumns = new byte[KeyboardColumns];

    private readonly Dictionary<Key, HeldKey> _heldKeys = [];

    private bool _hostLeftShiftHeld;
    private bool _hostRightShiftHeld;

    private readonly record struct HeldKey(int Column, int Row, bool Shift, bool Ctrl);

    public override void OnKeyEvent(KeyEvent keyEvent)
    {
        if (keyEvent.Key is Key.LeftShift or Key.RightShift)
        {
            // Host Shift never touches the matrix by itself: the BBC's SHIFT
            // is held by whichever keys need it, so a character that is
            // shifted on the host but not on the BBC (or vice versa) still
            // types correctly.
            if (keyEvent.Key == Key.LeftShift)
            {
                _hostLeftShiftHeld = keyEvent.IsDown;
            }
            else
            {
                _hostRightShiftHeld = keyEvent.IsDown;
            }

            return;
        }

        if (keyEvent.IsDown)
        {
            if (ResolveMatrixPosition(keyEvent, _hostLeftShiftHeld || _hostRightShiftHeld) is not { } position)
            {
                return;
            }

            _heldKeys[keyEvent.Key] = position with { Ctrl = keyEvent.Ctrl };
        }
        else if (!_heldKeys.Remove(keyEvent.Key))
        {
            return;
        }

        Array.Clear(_keyColumns);

        foreach (var held in _heldKeys.Values)
        {
            _keyColumns[held.Column] |= (byte)(1 << held.Row);

            if (held.Shift)
            {
                _keyColumns[0] |= 1 << 0;
            }

            if (held.Ctrl)
            {
                _keyColumns[1] |= 1 << 0;
            }
        }
    }

    // The character the host produced wins whenever the BBC has a key for
    // it, so the keycap legend is what gets typed: host Shift+' types '"'
    // (BBC SHIFT+2), host '=' types '=' (BBC SHIFT+-). A letter, which the
    // OS turns upper or lower case by Caps Lock and SHIFT, goes to its key
    // with host Shift passed through, as do the cursor and editing keys.
    private static HeldKey? ResolveMatrixPosition(KeyEvent keyEvent, bool hostShift)
    {
        if (keyEvent.Character is { } character && MapCharacter(character) is { } symbol)
        {
            return symbol;
        }

        var key = keyEvent.Key;

        // A letter typed as its own Key (Aemula.Console's --input sends 'P'
        // with no Shift event) belongs on the letter's key.
        if (char.IsAsciiLetterUpper((char)key))
        {
            key = (Key)char.ToLowerInvariant((char)key);
        }

        return MapKey(key) is { } position
            ? new HeldKey(position.Column, position.Row, hostShift, false)
            : null;
    }

    // Digits and punctuation, with the SHIFT state the BBC needs to print
    // each one (its shifted legends differ from a PC's: '=' is SHIFT+-, '+'
    // is SHIFT+;, '*' is SHIFT+: and so on).
    private static HeldKey? MapCharacter(char character)
    {
        var (position, shift) = character switch
        {
            '1' => ((0, 3), false),
            '2' => ((1, 3), false),
            '3' => ((1, 1), false),
            '4' => ((2, 1), false),
            '5' => ((3, 1), false),
            '6' => ((4, 3), false),
            '7' => ((4, 2), false),
            '8' => ((5, 1), false),
            '9' => ((6, 2), false),
            '0' => ((7, 2), false),
            '-' => ((7, 1), false),
            '^' => ((8, 1), false),
            '\\' => ((8, 7), false),
            '@' => ((7, 4), false),
            '[' => ((8, 3), false),
            ']' => ((8, 5), false),
            ';' => ((7, 5), false),
            ':' => ((8, 4), false),
            ',' => ((6, 6), false),
            '.' => ((7, 6), false),
            '/' => ((8, 6), false),
            '£' => ((8, 2), false),
            ' ' => ((2, 6), false),

            '!' => ((0, 3), true),
            '"' => ((1, 3), true),
            '#' => ((1, 1), true),
            '$' => ((2, 1), true),
            '%' => ((3, 1), true),
            '&' => ((4, 3), true),
            '\'' => ((4, 2), true),
            '(' => ((5, 1), true),
            ')' => ((6, 2), true),
            '=' => ((7, 1), true),
            '~' => ((8, 1), true),
            '|' => ((8, 7), true),
            '{' => ((8, 3), true),
            '}' => ((8, 5), true),
            '+' => ((7, 5), true),
            '*' => ((8, 4), true),
            '<' => ((6, 6), true),
            '>' => ((7, 6), true),
            '?' => ((8, 6), true),
            '_' => ((8, 2), true),

            _ => ((-1, -1), false),
        };

        return position.Item1 < 0 ? null : new HeldKey(position.Item1, position.Item2, shift, false);
    }

    // Column and row of each key that has no character of its own, and of
    // the letters.
    private static (int Column, int Row)? MapKey(Key key) => key switch
    {
        Key.Q => (0, 1),
        Key.W => (1, 2),
        Key.E => (2, 2),
        Key.R => (3, 3),
        Key.T => (3, 2),
        Key.Y => (4, 4),
        Key.U => (5, 3),
        Key.I => (5, 2),
        Key.O => (6, 3),
        Key.P => (7, 3),

        Key.A => (1, 4),
        Key.S => (1, 5),
        Key.D => (2, 3),
        Key.F => (3, 4),
        Key.G => (3, 5),
        Key.H => (4, 5),
        Key.J => (5, 4),
        Key.K => (6, 4),
        Key.L => (6, 5),

        Key.Z => (1, 6),
        Key.X => (2, 4),
        Key.C => (2, 5),
        Key.V => (3, 6),
        Key.B => (4, 6),
        Key.N => (5, 5),
        Key.M => (5, 6),

        Key.Space => (2, 6),
        Key.Return => (9, 4),
        Key.Backspace or Key.Delete => (9, 5),
        Key.Escape => (0, 7),
        Key.Left => (9, 1),
        Key.Down => (9, 2),
        Key.Up => (9, 3),
        Key.Right => (9, 7),
        Key.Home => (9, 6),

        _ => null,
    };

    // Presents the keyboard's outputs to the System VIA. Run before each
    // 1MHz bus cycle, by when the CPU's writes to port A and the latch from
    // earlier cycles have taken effect.
    private void UpdateKeyboard()
    {
        byte pa7 = 0;
        bool anyKeyDown;

        if (_ic32.Q3)
        {
            anyKeyDown = false;

            foreach (var column in _keyColumns)
            {
                anyKeyDown |= (column & 0xFE) != 0;
            }
        }
        else
        {
            var selection = SystemVia.PortA;
            var column = selection & 0x0F;
            var row = (selection >> 4) & 7;

            var keys = column < KeyboardColumns ? _keyColumns[column] : (byte)0;
            anyKeyDown = (keys & 0xFE) != 0;

            if ((keys & (1 << row)) != 0)
            {
                pa7 = 0x80;
            }
        }

        SystemVia.PA = pa7;
        SystemVia.Ca2 = anyKeyDown;
    }
}
