using System;
using System.Collections.Generic;

namespace Aemula.Emulation.Systems.ZX80;

// The keyboard diode matrix (D3-D10): plain wiring, not a chip class, per
// the plan's fidelity note - every switch's row side goes through a diode
// straight to an address line, and its column side feeds into IC10 (the
// 74LS365 "keyboard/cassette buffer"). Five of IC10's six buffers sit
// between a column and one of D0-D4, enabled by /KBD - exactly the
// "this is a keyboard-style I/O read" condition DoCpuMemoryAccess already
// gates ReadKeyboardMatrix on (GetKbdSignal, in ZX80System.Video.cs). A
// buffer just passes its column's level straight through unchanged whenever
// it's enabled, and it's only ever enabled during that same read, so
// computing the column bits directly here and placing them on Cpu.Data
// produces the identical byte without a second, redundant copy of that
// gating - the same pragmatic call the ROM/RAM address decode makes in
// ZX80System.cs. IC10's sixth buffer is the cassette EAR path (see
// ZX80System.Cassette.cs), which is why the read below pulls D7 from there
// instead of computing it locally.
//
// Row selection normally passes through IC6/7/8 (the same muxes
// ZX80System.Video.cs uses for the ROM address), which pick the CPU's own
// address over the video-generated one whenever /RFSH is deasserted - and
// it always is for the I/O read cycle a keyboard scan runs on, since /RFSH
// only ever pulses during an opcode fetch's refresh half. So the muxed and
// raw address bits are provably identical at the one moment this matters,
// and reading Cpu.Address's A8-A15 directly is the same signal without
// resurrecting mux channels whose other (video-mode) side is either unused
// here or, per the netlist's own diagnostics, unrecoverable from the scan.
public sealed partial class ZX80System
{
    // One 5-bit mask per row (A8..A15), bit N set means the key in that
    // row's column N is currently held down. Matches the standard Sinclair
    // matrix (confirmed against the netlist's own diode/switch nets, modulo
    // a few column pins the extractor mis-clustered onto row nets - the
    // clean majority of the matrix already matched this layout exactly) and
    // against the ROM's own unshifted key table at $006C, which lists every
    // non-SHIFT crosspoint in row/column order. Rebuilt from _heldKeys on
    // every key event rather than edited in place, since SHIFT's crosspoint
    // is shared by every held key that needs it.
    private readonly byte[] _keyMatrixRows = new byte[8];

    // Every host key currently holding a crosspoint down, keyed by its
    // modifier-independent identity so a key-up releases what its key-down
    // pressed even if host Shift changed in between - and each remembering
    // whether it also needs the ZX80's SHIFT, decided once at key-down.
    private readonly Dictionary<Key, (int Row, int Column, bool Shift)> _heldKeys = [];

    private bool _hostLeftShiftHeld;
    private bool _hostRightShiftHeld;

    public override void OnKeyEvent(KeyEvent keyEvent)
    {
        if (keyEvent.Key is Key.LeftShift or Key.RightShift)
        {
            // Host Shift never touches the matrix by itself: the ZX80's
            // SHIFT crosspoint is driven by whichever held keys need it, so
            // a host character that's shifted on the host but not on the
            // ZX80 (or vice versa) still types the right thing.
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
            _heldKeys[keyEvent.Key] = position;
        }
        else if (!_heldKeys.Remove(keyEvent.Key))
        {
            return;
        }

        Array.Clear(_keyMatrixRows);
        foreach (var (row, column, shift) in _heldKeys.Values)
        {
            _keyMatrixRows[row] |= (byte)(1 << column);
            if (shift)
            {
                _keyMatrixRows[0] |= 1;
            }
        }
    }

    // The character the host produced wins whenever the ZX80 keyboard has a
    // key for it, so the host keycap legend is what gets typed: host
    // Shift+' types '"' (ZX80 SHIFT+Y), host '=' types '=' (ZX80 SHIFT+L).
    // Anything the ZX80 has no key for - uppercase letters, '!', '@' and so
    // on - falls back to the host key's position on the ZX80 matrix with
    // host Shift passed through, which keeps every SHIFT legend that has no
    // host character reachable: Shift+A-T for the block graphics, Shift+1-4
    // for NOT/AND/THEN/TO, Shift+B for OR, Shift+H for **, Shift+Return for
    // EDIT. The cursor keys, HOME and RUBOUT live on SHIFT+5-9/0, whose host
    // characters on most layouts ('%', '(', ')' ...) resolve as symbols
    // instead, so the host's own arrow/Home/Backspace keys drive those.
    private static (int Row, int Column, bool Shift)? ResolveMatrixPosition(KeyEvent keyEvent, bool hostShift)
    {
        if (keyEvent.Character is { } character && MapCharacterToMatrixPosition(character) is { } symbol)
        {
            return symbol;
        }

        switch (keyEvent.Key)
        {
            case Key.Left: return (3, 4, true);  // SHIFT+5
            case Key.Down: return (4, 4, true);  // SHIFT+6
            case Key.Up: return (4, 3, true);    // SHIFT+7
            case Key.Right: return (4, 2, true); // SHIFT+8
            case Key.Home: return (4, 1, true);  // SHIFT+9
            case Key.Backspace or Key.Delete: return (4, 0, true); // SHIFT+0, RUBOUT
        }

        // A letter typed without a host Shift event (Aemula.Console's
        // --input, say, which sends 'P' as its own Key) still belongs on the
        // letter's key - the ZX80 has only one letter case.
        var key = keyEvent.Character is { } letter && char.IsAsciiLetter(letter)
            ? (Key)char.ToLowerInvariant(letter)
            : keyEvent.Key;

        return MapKeyToMatrixPosition(key) is { } position
            ? (position.Row, position.Column, hostShift)
            : null;
    }

    // The ZX80's digits and punctuation, including the SHIFT legends that
    // print as ordinary characters (the ROM's shifted key table at $0093;
    // a few of them - ';', '/', '*', '=', '+', '-', '<', '>', ',' - arrive
    // as single-character tokens rather than character codes, but display
    // identically). Digits are here rather than left to the positional
    // fallback so a layout that shifts its digits (AZERTY) doesn't turn
    // them into SHIFT+digit.
    private static (int Row, int Column, bool Shift)? MapCharacterToMatrixPosition(char character) => character switch
    {
        >= '1' and <= '5' => (3, character - '1', false),
        '0' => (4, 0, false),
        >= '6' and <= '9' => (4, '9' - character + 1, false),
        '.' => (7, 1, false),

        ':' => (0, 1, true),
        ';' => (0, 2, true),
        '?' => (0, 3, true),
        '/' => (0, 4, true),
        '*' => (5, 0, true),
        ')' => (5, 1, true),
        '(' => (5, 2, true),
        '$' => (5, 3, true),
        '"' => (5, 4, true),
        '=' => (6, 1, true),
        '+' => (6, 2, true),
        '-' => (6, 3, true),
        '£' => (7, 0, true),
        ',' => (7, 1, true),
        '>' => (7, 2, true),
        '<' => (7, 3, true),

        _ => null,
    };

    // Row 0 is A8 (D3's row) through row 7 A15 (D10's row); column 0 is D0
    // through column 4 D4 - the same left-to-right order the netlist's
    // clean column assignments show for every row (SHIFT/A/Q/1/0/P/NEWLINE/
    // SPACE always land on D0, and so on rightward). SHIFT itself has no
    // entry: OnKeyEvent drives its crosspoint from the held keys instead.
    private static (int Row, int Column)? MapKeyToMatrixPosition(Key key) => key switch
    {
        Key.Z => (0, 1),
        Key.X => (0, 2),
        Key.C => (0, 3),
        Key.V => (0, 4),

        Key.A => (1, 0),
        Key.S => (1, 1),
        Key.D => (1, 2),
        Key.F => (1, 3),
        Key.G => (1, 4),

        Key.Q => (2, 0),
        Key.W => (2, 1),
        Key.E => (2, 2),
        Key.R => (2, 3),
        Key.T => (2, 4),

        Key.Digit1 => (3, 0),
        Key.Digit2 => (3, 1),
        Key.Digit3 => (3, 2),
        Key.Digit4 => (3, 3),
        Key.Digit5 => (3, 4),

        Key.Digit0 => (4, 0),
        Key.Digit9 => (4, 1),
        Key.Digit8 => (4, 2),
        Key.Digit7 => (4, 3),
        Key.Digit6 => (4, 4),

        Key.P => (5, 0),
        Key.O => (5, 1),
        Key.I => (5, 2),
        Key.U => (5, 3),
        Key.Y => (5, 4),

        Key.Return => (6, 0),
        Key.L => (6, 1),
        Key.K => (6, 2),
        Key.J => (6, 3),
        Key.H => (6, 4),

        Key.Space => (7, 0),
        (Key)'.' => (7, 1),
        Key.M => (7, 2),
        Key.N => (7, 3),
        Key.B => (7, 4),

        _ => null,
    };

    // Reads whichever address lines are driven low: each selected row ANDs
    // its pressed keys' columns into the result (open-collector-style, via
    // the diodes) while every other bit stays pulled high - see above for
    // why this skips instantiating IC10's column-side buffers explicitly.
    // D5 has no keyboard connection so it stays forced high; D7 is EAR, read
    // live off IC10's sixth buffer (see ZX80System.Cassette.cs) - guaranteed
    // driven rather than floating, since that buffer is only ever enabled
    // for exactly the read this method runs on. D6 is D11, the NTSC strap
    // diode, fitted only on the US board - unlike a key row, it isn't gated
    // by which row address is selected, since the diode sits directly on the
    // data line rather than behind a row/column crosspoint: every
    // keyboard-style read pulls it low, which is the signal the ROM's own
    // code branches on to run 262-line/60Hz timing instead of the
    // 312-line/50Hz default. Without the diode D6 is pulled high.
    private byte ReadKeyboardMatrix(ushort address)
    {
        var columns = 0x1F;

        for (var row = 0; row < 8; row++)
        {
            if ((address & (0x100 << row)) == 0)
            {
                columns &= ~_keyMatrixRows[row];
            }
        }

        var ear = _cassetteBuffer.Y1 == true ? 0x80 : 0x00;
        var d6 = _d11Fitted ? 0x00 : 0x40;
        return (byte)(0x20 | d6 | ear | columns);
    }

    internal byte ReadKeyboardMatrixForTest(ushort address) => ReadKeyboardMatrix(address);
}
