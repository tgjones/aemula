using Aemula.Emulation.Chips;

namespace Aemula.Emulation.Systems.AppleI;

// The dot path: ICC3 (the 2519 line buffer) addresses ICD2 (the 2513
// character generator) whose five row-dot outputs are parallel-loaded into
// ICD1 (74166) and shifted out at the dot clock. Wiring from
// docs/apple-i-netlist.txt:
//
//   ICD2 A1-A3 (pins 14-16) = V0-V2, the scanline within the glyph row;
//   A4-A9 (pins 17-22) = ICC3's six outputs. ICD2 O1-O5 (pins 4-8) feed
//   ICD1's D-H (pins 5, 10, 11, 12, 14); ICD1's A, B, C and SER are
//   grounded, so each cell shifts out five glyph dots and then blanks.
//   ICD1 is clocked at the dot rate on its CLK INHIBIT pin (6) with CLK (7)
//   grounded - the 74166 ORs those two internally, so it's simply the dot
//   clock. SH/LD (pin 15) is ICD10:A = NAND(ICD11.RCO, H6): a one-dot load
//   strobe on the sixth dot of every character-time inside the 40-column
//   window. Loaded on that edge, QH shows H (O5) immediately and G, F, E, D
//   (O4-O1) on the four shifts that follow, then the grounded C and B for the
//   two dots before the next load: a 7-dot cell, five lit and two blank.
//   Outside the window nothing is ever loaded, and the grounded SER shifts
//   zeros in, so the dot output is low through blanking and sync.
public sealed partial class AppleISystem
{
    private readonly Ttl74166Chip _icd1 = new();

    // QH (pin 13), through R1 into the video mixer -
    // AppleISystem.CompositeVideo.cs.
    public bool VideoBit => _icd1.Qh;

    private void InitializePixelShiftRegister()
    {
        _icd1.A = false;
        _icd1.B = false;
        _icd1.C = false;
        _icd1.Ser = false;
        _icd1.ClkInh = false;
    }

    // Called on every dot-clock rising edge, before ICD11 counts, so the
    // load strobe and the 2519 outputs it samples are the pre-edge levels.
    private void TickPixelShiftRegister()
    {
        // ICD10:A: SH/LD = NAND(ICD11.RCO, H6).
        _icd10.A1 = _icd11.Rco;
        _icd10.B1 = _icd7.Qc;
        var shiftLoadBar = _icd10.Y1;
        _icd1.ShLd = shiftLoadBar;

        // The 2513's outputs only reach anything through ICD1's parallel
        // inputs, which it samples on a load edge alone, so the character
        // generator is only consulted on those (one dot in seven).
        if (!shiftLoadBar)
        {
            _characterGenerator.Address1 = _icd8.Qa; // V0
            _characterGenerator.Address2 = _icd8.Qb; // V1
            _characterGenerator.Address3 = _icd8.Qc; // V2
            _characterGenerator.Address4 = _lineBuffer.Out1;
            _characterGenerator.Address5 = _lineBuffer.Out2;
            _characterGenerator.Address6 = _lineBuffer.Out3;
            _characterGenerator.Address7 = _lineBuffer.Out4;
            _characterGenerator.Address8 = _lineBuffer.Out5;

            // Address9 is the 2513's weight-0x20 code bit, and it arrives
            // inverted relative to the stored code: ICC10:A NORs the RD7
            // plane into the 2519's sixth input
            // (AppleISystem.CharacterMemory.cs) and nothing undoes that. It
            // is the intended mapping: an all-zeros cell (power-on, and
            // everything CLEAR SCREEN or the CR line-fill writes) lands on
            // the 2513's space glyph ($20) rather than '@' ($00), while
            // WozMon's echoed ASCII still resolves to the right glyphs ('\'
            // -> $1C, '5' -> $35, 'A' -> $01) and the cursor cell alternates
            // '@'/blank with the 555.
            _characterGenerator.Address9 = _lineBuffer.Out6;

            _icd1.D = _characterGenerator.Out1 == true;
            _icd1.E = _characterGenerator.Out2 == true;
            _icd1.F = _characterGenerator.Out3 == true;
            _icd1.G = _characterGenerator.Out4 == true;
            _icd1.H = _characterGenerator.Out5 == true;
        }

        _icd1.Clk = false;
        _icd1.Clk = true;
    }
}
