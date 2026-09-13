namespace Aemula.Emulation.Systems.ZX80;

// The keyboard diode matrix (D3-D10): plain wiring, not a chip class, per
// the plan's fidelity note. Tracing the hand-derived netlist turned up that
// IC10 (the 74LS365, labelled "keyboard/cassette buffer" going in) actually
// takes no part in keyboard sensing at all - every switch's row side goes
// through a diode straight to an address line, and its column side ties
// directly onto the CPU's own data bus (D0-D4), pulled up by R10-R17 when
// open. IC10's inputs and outputs land on those same D-bus nets with no
// keyboard connection anywhere near it, so it's reserved entirely for the
// cassette EAR wiring due in phase 5, and the row/column read below is
// wired straight off Cpu.Address/Cpu.Data instead of through it.
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
    // clean majority of the matrix already matched this layout exactly).
    private readonly byte[] _keyMatrixRows = new byte[8];

    public override void OnKeyEvent(KeyEvent keyEvent)
    {
        var position = MapKeyToMatrixPosition(keyEvent.Key);
        if (position is null)
        {
            return;
        }

        var (row, column) = position.Value;
        var bit = (byte)(1 << column);

        if (keyEvent.IsDown)
        {
            _keyMatrixRows[row] |= bit;
        }
        else
        {
            _keyMatrixRows[row] &= (byte)~bit;
        }
    }

    // Row 0 is A8 (D3's row) through row 7 A15 (D10's row); column 0 is D0
    // through column 4 D4 - the same left-to-right order the netlist's
    // clean column assignments show for every row (SHIFT/A/Q/1/0/P/NEWLINE/
    // SPACE always land on D0, and so on rightward).
    private static (int Row, int Column)? MapKeyToMatrixPosition(Key key) => key switch
    {
        Key.LeftShift or Key.RightShift => (0, 0),
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
        Key.B => (7, 1),
        Key.M => (7, 2),
        Key.N => (7, 3),

        _ => null,
    };

    // The 74LS365's absence (see above) means this is a plain read of
    // whichever address lines are driven low: each selected row ANDs its
    // pressed keys' columns into the result (open-collector-style, via the
    // diodes) while every other bit stays pulled high. D5-D7 have no
    // keyboard connection at all - D6 gets the NTSC strap diode in phase 6,
    // D7 the cassette EAR input in phase 5, both idle-high until then.
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

        return (byte)(0xE0 | columns);
    }

    internal byte ReadKeyboardMatrixForTest(ushort address) => ReadKeyboardMatrix(address);
}
