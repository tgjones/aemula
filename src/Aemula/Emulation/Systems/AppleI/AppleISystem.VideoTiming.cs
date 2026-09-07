using Aemula.Emulation.Chips;

namespace Aemula.Emulation.Systems.AppleI;

// The horizontal/vertical counter chain (ICD6-ICD9 on the schematic) that
// generates composite sync and the character-generator row address. Traced
// from http://retro.hansotten.nl/uploads/apple1/a1%20circuit.pdf (Terminal
// Section, sheet 1 of 3) and the extracted netlist.
//
// ICD6 (74160) + ICD7 (74161): the horizontal character-position counter,
// synchronously cascaded (ICD7's ENT tied to ICD6's RCO) off one shared
// clock. That clock is the 14.31818MHz dot clock divided by 14 - done
// directly here rather than as a gate-for-gate reproduction, because the
// traceable wire leaves sheet 1 as a labelled net ("O2") that on sheet 2
// disappears into a jumper-selectable 7404/transistor cluster only populated
// for a 6800 substitution (schematic Note 7). 14.31818MHz / 14 is exactly
// the 6502's real 1.022727MHz with no remainder - unlike Apple II there's no
// long-cycle stretch to reproduce. The two counters' preset-reload values
// (ICD6 = 5, ICD7 = 9) land on a clean 65-character-time line, matching
// NTSC's 15734Hz line rate; HSync/VSync are decoded from the resulting
// counts rather than the exact gate netlist (ICC5:A/ICC9:B/ICC10 and
// friends), which are illegible at the available render resolution.
//
// ICD8 (74161) + ICD9 (74161): the vertical counter - "V0-V5" on the
// schematic plus ICD9's two top bits - one 8-bit line-in-frame count
// clocked once per line (ICD8's ENT tied to LASTH, ICD7's end-of-line RCO),
// ICD9 cascaded off ICD8's RCO. V0-V2 are the row-within-glyph address that
// feeds the 2513 character generator's A1-A3 and the ICB2:B NAND that gates
// the recirculating rings and the 2519 line buffer (see
// AppleISystem.CharacterMemory.cs). ICD9's top two bits (V6/V7) are decoded
// by ICD10:C into /VBL.
//
// The real board also runs ICD8/ICD9's ENP off VINH (ICD15's QB) and can
// reload them mid-count from ICC9:C's "/VBL and /WC1" term - that reload is
// the vertical-scroll mechanism and depends on /WC1 from the write-cursor
// state machine, which isn't built yet, so it's omitted here: the counter
// free-runs mod 256, which is the correct behaviour for a screen that hasn't
// scrolled, and VINH is treated as permanently enabling.
//
// /VBL (ICD10:C, 7400): NAND(V6, V7) - pulled low (blanking) once the line
// count reaches 0xC0 (192), i.e. after the 192 visible scanlines (24 rows x
// 8 scanlines), and back high for the 64-line vertical-blank interval.
public sealed partial class AppleISystem
{
    // ICD6/ICD7: horizontal character-position counter. Cascaded (ICD7.Ent =
    // ICD6.Rco) so together they act as one mod-65 counter - 65 being the
    // confirmed character-times-per-line count (14.31818MHz / 14 dots per
    // character-time / 65 character-times per line lands exactly on NTSC's
    // 15734.26Hz line rate).
    private readonly Ttl74160Chip _horizontalCounterLow;
    private readonly Ttl74161Chip _horizontalCounterHigh;

    // ICD8/ICD9: the vertical line-in-frame counter ("V0-V5" plus ICD9's two
    // top bits). Cascaded (ICD9.Ent = ICD8.Rco), clocked once per line off
    // ICD7's end-of-line RCO. Free-running mod 256 - see the file header on
    // the omitted VINH gate and scroll reload.
    private readonly Ttl74161Chip _lineCounterLow;
    private readonly Ttl74161Chip _lineCounterHigh;

    // ICD10:C: /VBL generator - NAND of the line counter's two top bits, so
    // /VBL falls once the line count reaches 192 (0xC0) and stays low for the
    // rest of the 256-line frame.
    private readonly Ttl7400Chip _verticalBlankGate = new();

    // Divides the 14.31818MHz dot clock by 14 into the shared clock for the
    // CPU and the counters above - see the file header for why this is done
    // directly rather than as a traced gate chain.
    private byte _dotDivider;
    private bool _lastCharacterRate;

    // End-of-line / end-of-frame strobes for the character-memory state
    // machine (LASTH / LAST on the schematic). ICC7 in
    // AppleISystem.CharacterMemory.cs registers them on the MEM0 edge
    // (D5->Q5 = _S1_105, D1->Q1 = _S1_101) to drive the write-cursor advance
    // and the CR line-fill. Set at the end of TickCounters - see there.
    private bool _endOfLine;
    private bool _endOfFrame;

    public bool HSync => HorizontalCount >= 155;

    public bool VSync => VerticalCount >= 252;

    public bool CompositeSync => HSync || VSync;

    internal bool Phi0ForTests => _dotDivider < 7;

    // The horizontal counters' reload target (see TickCounters below) - the
    // first of the 65 character-times in a line, and so the start of
    // AppleISystem.Video.cs's 40-character active window.
    private const int HorizontalActiveStart = 95;

    private int HorizontalCount =>
        NibbleValue(_horizontalCounterHigh.Qa, _horizontalCounterHigh.Qb, _horizontalCounterHigh.Qc, _horizontalCounterHigh.Qd) * 10 +
        NibbleValue(_horizontalCounterLow.Qa, _horizontalCounterLow.Qb, _horizontalCounterLow.Qc, _horizontalCounterLow.Qd);

    // ICD8/ICD9 read as one 8-bit line-in-frame count.
    private int VerticalCount =>
        (NibbleValue(_lineCounterHigh.Qa, _lineCounterHigh.Qb, _lineCounterHigh.Qc, _lineCounterHigh.Qd) << 4) |
        NibbleValue(_lineCounterLow.Qa, _lineCounterLow.Qb, _lineCounterLow.Qc, _lineCounterLow.Qd);

    // Row-within-glyph (V0-V2): the 2513 character generator's A1-A3 scanline
    // address, taken straight off ICD8's low three outputs.
    private int GlyphRow =>
        (_lineCounterLow.Qa ? 1 : 0) | (_lineCounterLow.Qb ? 2 : 0) | (_lineCounterLow.Qc ? 4 : 0);

    // /VBL (ICD10:C): NAND(V6, V7). Low during the vertical-blank interval.
    private bool NotVerticalBlank
    {
        get
        {
            _verticalBlankGate.A1 = _lineCounterHigh.Qc; // V6
            _verticalBlankGate.B1 = _lineCounterHigh.Qd; // V7
            return _verticalBlankGate.Y1;
        }
    }

    private static int NibbleValue(bool qa, bool qb, bool qc, bool qd) =>
        (qa ? 1 : 0) | (qb ? 2 : 0) | (qc ? 4 : 0) | (qd ? 8 : 0);

    private void TickVideoTiming()
    {
        var characterRate = _dotDivider < 7;
        var characterRateRisingEdge = characterRate && !_lastCharacterRate;
        _lastCharacterRate = characterRate;

        Cpu.Phi0 = characterRate;

        if (characterRateRisingEdge)
        {
            DoCpuMemoryAccess();
            TickVideo();
            TickCounters();
            TickCharacterMemory();
        }

        TickVideoDot();
        TickCompositeVideo();

        _dotDivider++;
        if (_dotDivider == 14)
        {
            _dotDivider = 0;
        }
    }

    private void TickCounters()
    {
        _horizontalCounterLow.Enp = true;
        _horizontalCounterLow.Ent = true;

        _horizontalCounterHigh.Enp = true;
        _horizontalCounterHigh.Ent = _horizontalCounterLow.Rco;

        // Reloads both stages together the tick after the combined count
        // hits its natural max (159), landing back on a preset (95) chosen
        // so the wrap spans exactly 65 states - see the file header. This
        // same signal is LASTH, the end-of-line strobe that clocks the
        // vertical counter.
        var lastH = _horizontalCounterHigh.Rco;

        _horizontalCounterLow.Load = !lastH;
        _horizontalCounterLow.A = true;  // preset 5 = 0b0101
        _horizontalCounterLow.B = false;
        _horizontalCounterLow.C = true;
        _horizontalCounterLow.D = false;

        _horizontalCounterHigh.Load = !lastH;
        _horizontalCounterHigh.A = true; // preset 9 = 0b1001
        _horizontalCounterHigh.B = false;
        _horizontalCounterHigh.C = false;
        _horizontalCounterHigh.D = true;

        // Vertical line counter: ICD8 advances once per line (ENT = LASTH),
        // ICD9 cascaded off ICD8's ripple carry. No parallel load - the
        // scroll reload is omitted (file header) - so it free-runs mod 256.
        _lineCounterLow.Enp = true;
        _lineCounterLow.Ent = lastH;
        _lineCounterLow.Load = true;

        _lineCounterHigh.Enp = true;
        _lineCounterHigh.Ent = _lineCounterLow.Rco;
        _lineCounterHigh.Load = true;

        PulseClock(_horizontalCounterLow);
        PulseClock(_horizontalCounterHigh);
        PulseClock(_lineCounterLow);
        PulseClock(_lineCounterHigh);

        // LASTH / LAST for the character-memory state machine, read from the
        // post-clock counts - the same view EvaluateRingClock's H6/V0-V2
        // decode uses, so the character-time this is asserted is also one
        // MEM0 clocks ICC7 on and _S1_105 can actually capture it.
        //
        // Asserted one MEM0 shift before the horizontal counter's terminal
        // count (159), not on it: after ICC7's two-stage
        // LASTH -> _S1_105 -> /WC1 -> /WC2 pipeline the cursor re-seat then
        // lands exactly on column 0 of the next row. The precise place LASTH
        // sits within the 40-shift active window is one of the horizontal
        // decode values still taken empirically rather than enumerated from
        // ICD6/ICD7's presets.
        _endOfLine = HorizontalCount == 158;
        _endOfFrame = _endOfLine &&
            _lineCounterLow.Qa && _lineCounterLow.Qb && _lineCounterLow.Qc && _lineCounterLow.Qd &&
            _lineCounterHigh.Qa && _lineCounterHigh.Qb && _lineCounterHigh.Qc && _lineCounterHigh.Qd;
    }

    private static void PulseClock(Ttl74160Chip chip)
    {
        chip.Clk = false;
        chip.Clk = true;
    }

    private static void PulseClock(Ttl74161Chip chip)
    {
        chip.Clk = false;
        chip.Clk = true;
    }
}
