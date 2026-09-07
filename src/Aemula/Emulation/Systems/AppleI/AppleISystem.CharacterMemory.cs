using Aemula.Emulation.Chips;

namespace Aemula.Emulation.Systems.AppleI;

// The character-memory delay line: seven Signetics2504Chip (six character-
// code bit-planes plus one cursor tracker), one Signetics2519Chip line
// buffer, a Ds0025Chip two-phase clock driver, and the write mechanism that
// lets the CPU inject a new character into the recirculating stream at the
// cursor's position - designators ICC3/ICC4/ICC11A/ICC11B/ICC14/ICD4A/
// ICD4B/ICD5A/ICD5B/ICD14A/ICD14B on the schematic.
//
// The CPU-side handshake is gate-level: ICC15:D inverts the PIA's CB2 into
// the active-high busy flag DA (read back on PB7); ICC7 (74174), clocked by
// MEM0, registers DA and the write-commit term _S1_71 (from ICC6:B); ICC7's
// /RDA output fires the ICB3:B one-shot, whose pulse drives CB1 to restore
// CB2 and clear busy once a character has latched into the rings.
//
// Confirmed from the schematic (http://retro.hansotten.nl/uploads/apple1/a1
// %20circuit.pdf, Terminal Section): ICC4 and ICC14 are 74157 quad 2:1
// muxes whose S select is a net drawn with an overline ("WRITE-bar") and
// whose OE-bar is tied to a net named "CLR" - i.e. the mux passes PIA
// display data (RD1-RD4 into ICC4; RD5/RD7 into two of ICC14's four
// channels) through to each 2504's IN pin when WRITE-bar is false (a write
// is committing this cycle), and each 2504's own OUT (self-recirculate)
// through when WRITE-bar is true. One PIA data bit (RD6/PB5) is genuinely
// not wired to either mux - six of the seven PIA display bits carry the
// character code, not all seven. ICC14 also has a spare channel wired
// I0=GND/I1=CURS, i.e. it forces the cursor bit to 0 during a write and
// passes it through unchanged otherwise - confirmed directly from the
// schematic. What ICC14's spare channel doesn't explain on its own is how
// the cursor bit gets set again one position later: Chris Espinosa (an
// original Apple engineer, quoted at
// https://www.righto.com/2022/04/inside-apple-1s-shift-register-memory.html)
// describes "flip-flops 2 and 3 at C13 [ICC13, a 74175] re-set the cursor
// bit on the next character clock; this advances the cursor one position" -
// confirmed as involving ICC13 and an AND gate (ICC12:C) producing a net
// named "WC2" from the schematic, but the exact gate truth table wasn't
// fully pinned down from the rendered schematic tiles in the time available.
// Reproduced here as equivalent C# sequencing (clear on the write cycle, set
// exactly one character-clock later) rather than a literal gate netlist.
public sealed partial class AppleISystem
{
    // ICD5A, ICD5B, ICD4A, ICD4B, ICD14A, ICD14B - the six character-code
    // bit-planes. Index order is this class's own choice (not confirmed
    // pin-for-pin from the schematic beyond "six of PB0-PB6, skipping
    // PB5") - see CharacterDataPiaBit below. Self-consistent between write
    // and read, which is what correctness actually depends on.
    private readonly Signetics2504Chip[] _characterBits =
    [
        new Signetics2504Chip(),
        new Signetics2504Chip(),
        new Signetics2504Chip(),
        new Signetics2504Chip(),
        new Signetics2504Chip(),
        new Signetics2504Chip(),
    ];

    // ICC11B - the seventh 2504, carrying a single circulating 1-bit cursor
    // marker rather than a character-code bit-plane (see the file header).
    private readonly Signetics2504Chip _cursorBit = new();

    // ICC3 - buffers the 40 character codes of whatever row the character-
    // code bit-planes most recently output. Its IN pins follow the
    // character bits' OUT pins (matching the schematic), and its own
    // Recirculate/Clock pins are driven for real below by TickLineBufferClock
    // - AppleISystem.Video.cs reads its OUT pins directly, the same way the
    // real 2513 character generator's A4-A9 address pins do.
    private readonly Signetics2519Chip _lineBuffer = new();

    // ICD11 - a free-running 74161 that produces the fast "CL" bit combined
    // into LINE0 below. Confirmed from the schematic: CET/CEP/PE (the 74161's
    // ENT/ENP/LOAD-bar) are all tied to its own weight-8 output (Qd), and
    // P0/P2 are grounded while P1/P3 are left floating (floating TTL inputs
    // pull high), giving preset 1010 = 10 - so it self-reloads to 10
    // whenever it counts past 15 (wraps to 0, at which point Qd=0 forces the
    // reload on the next edge), cycling 10,11,12,13,14,15,0 - a 7-character-
    // time period. Real hardware clocks it from ICD1.QH (the pixel shift
    // register's own last stage, pulsing once per character-time); clocking
    // it once per TickCharacterMemory call here is equivalent, since that's
    // already gated to the same characterRateRisingEdge.
    private readonly Ttl74161Chip _lineBufferClockCounter = new();

    // ICC11A - two-phase clock driver for the whole bank above. Its two
    // channels invert (see Ds0025Chip's remarks); that inversion has no
    // observable effect here since nothing but this file's own Phi1/Phi2
    // pulses ever reads Out1/Out2.
    private readonly Ds0025Chip _shiftClockDriver = new();

    // The MEM0 gate chain (ICC5:A/ICD10:B/ICC10:C/ICB2:B) that decides which
    // character-times the recirculating rings actually shift on, replacing
    // an unconditional shift every character-time. MEM0 works out to exactly
    // 1024 asserted character-times per frame - one full ring rotation:
    //  * 40 per row on each glyph's last scanline, across the 24 visible
    //    rows  (960), plus
    //  * 8 per line on the 8 blanked glyph-rows whose V0-V2 count still
    //    reads "last scanline"  (64).
    //
    // ICB2:B (7410): _S1_97 = NAND(V0,V1,V2) - low only on a glyph row's
    // last scanline. Also the 2519 line buffer's Recirculate control
    // (ICC3.4), so it's wired once and read by both.
    private readonly Ttl7410Chip _glyphRowLastLineGate = new();

    // ICD10:B (7400): LINE0 = NAND(H6, CL). H6 (ICD7's weight-4 output) is
    // the 40-character active window. CL is ICD11's dot-rate strobe that
    // chops that window into 40 discrete column clocks; evaluated once per
    // character-time it is always mid-strobe, so it's tied high here. Its
    // only other consumer, the 2519's own clock, still gets a real edge per
    // character-time from TickLineBufferClock.
    private readonly Ttl7400Chip _line0Gate = new();

    // ICC10:C (7402): _S1_0 = NOR(ICD6.Qd, /VBL). During active video /VBL
    // is high so _S1_0 is 0 and doesn't gate MEM0; during vertical blank it
    // follows !ICD6.Qd, which is what trims the blanked-row contribution to
    // 8 character-times per line.
    private readonly Ttl7402Chip _memBlankingGate = new();

    // ICC5:A (7427): MEM0 = NOR(_S1_0, LINE0, _S1_97).
    private readonly Ttl7427Chip _memClockGate = new();

    // ICC4 (four channels, driving the first four character bit-planes) and
    // ICC14 (two of its four channels, driving the remaining two - the
    // other two are the schematic's cursor-clear channel, reproduced
    // separately below rather than through this chip; see the file header).
    private readonly Ttl74157Chip _writeMuxA = new();
    private readonly Ttl74157Chip _writeMuxB = new();

    // ICC15:D (7400, drawn on the processor sheet): DA = NAND(CB2, CB2) =
    // NOT(CB2). The PIA pulls its CB2 handshake line low the instant the CPU
    // commits a write to $D012/ORB and holds it there until the write is
    // acknowledged; this inverter turns that into the active-high busy level
    // that reaches PB7 - the bit WozMon's ECHO polls (it spins while bit 7
    // reads 1 and writes once it reads 0, i.e. bit 7 is busy, not ready).
    private readonly Ttl7400Chip _displayBusyInverter = new();

    // ICC7 (74174), clocked by MEM0 - the same gated clock the recirculating
    // rings shift on. D2->Q2 registers DA one ring-clock late (net _S1_148);
    // D3->Q3 registers _S1_71 as /RDA, the write-acknowledge strobe. Feeding
    // the write path a registered, one-ring-clock-late view of the busy flag
    // rather than the live one is what widens the commit window enough for
    // WozMon's back-to-back busy poll to actually catch a write in flight.
    // (The four other flip-flops carry end-of-line/frame and cursor-advance
    // signals not yet wired here.)
    private readonly Ttl74174Chip _writeAckRegister = new();

    // ICC6:B (7410): _S1_71 = NAND(CURS, _S1_148, _S1_148) - low exactly
    // when the cursor bit is at the write point and the registered busy flag
    // is still set, i.e. a character is committing into the rings on this
    // ring clock.
    private readonly Ttl7410Chip _writeCommitGate = new();

    // ICB3:B (74123, also on the processor sheet): the write-acknowledge
    // one-shot. A falling edge on /RDA fires a fixed ~3.5us pulse whose
    // trailing edge releases CB1; CB1's active (rising) transition is what
    // tells the PIA to restore CB2 high, dropping the busy flag once the new
    // character has had time to latch into the shift register.
    private readonly Ttl74123Chip _writeAckOneShot = new()
    {
        // Pin 10 (2B) is tied high; the pulse width models R11/C-network's
        // ~3.5us at the 1.0227MHz character rate (rounded up to a whole
        // character-time, erring toward the CPU seeing busy a touch longer).
        B2 = true,
        PulseTicks2 = 4,
    };

    // Which PIA PB bit (of PB0-PB6) each entry of _characterBits reads from
    // - PB5 (RD6 on the schematic) is the one bit that's genuinely not
    // wired to either write mux; see the file header.
    private static readonly int[] CharacterDataPiaBit = [0, 1, 2, 3, 4, 6];

    // True for exactly the one character-clock following a committed write -
    // see the file header's ICC13/WC2 discussion.
    private bool _cursorSetPending;

    // Bookkeeping only - not extra hardware state. The real chips only ever
    // know "the bit at Out right now"; this just names which of the 1024
    // ring positions that bit is, so AppleISystem.Video.cs's simplified
    // direct-index read (see that file) can find the same character a write
    // here just landed on. Advanced only on a real MEM0-gated shift (see
    // ShiftCharacterRings), so it completes exactly one rotation per frame.
    private int _ringPosition;

    internal bool CursorOutForTests => _cursorBit.Out;
    internal int RingPositionForTests => _ringPosition;

    // A pure recirculating register has no power-on state of its own to
    // fall back on (see Signetics2504Chip.Poke's remarks) - real hardware
    // must have some reset-time path that seeds the cursor ring with
    // exactly one '1' bit, or CURS could never become true and nothing
    // could ever be typed. Seeded at the position _ringPosition (itself
    // reset to 0 by field initialization) will read as row 0, column 0 on
    // the very next character-time, so the cursor starts at the top-left
    // of the screen.
    private void ResetCharacterMemory()
    {
        foreach (var chip in _characterBits)
        {
            chip.Clear();
        }

        _cursorBit.Clear();
        _cursorBit.Poke(1023, true);

        _cursorSetPending = false;
        _ringPosition = 0;

        // Seed ICC7.Q3 (/RDA) high - its idle state, since _S1_71 is a NAND
        // that sits high whenever the cursor bit isn't at the write point.
        // Without this the register powers up with Q3 low and the very first
        // sample would look like a write-acknowledge strobe to the one-shot.
        _writeAckRegister.D3 = true;
        _writeAckRegister.Clk = false;
        _writeAckRegister.Clk = true;
    }

    private void TickCharacterMemory()
    {
        // MEM0 (ICC5:A) - the ring shift clock. The write mux select and the
        // ring recirculation are the same clocked event on real hardware, so
        // a pending character can only be committed on a character-time the
        // rings are actually shifting.
        var ringClock = EvaluateRingClock();

        // DA (ICC15:D) - the live busy level, just the inverse of the PIA's
        // CB2 handshake line.
        var displayBusy = DisplayBusy();

        // CURS - approximated as the cursor ring's own Out. The real net is
        // ICC13's registered copy of it, one character-clock behind; that
        // indirection lands with the cursor-advance logic and isn't modelled
        // yet.
        var cursorHere = _cursorBit.Out;

        // _S1_71 (ICC6:B): NAND(CURS, _S1_148, _S1_148), low only when the
        // cursor is at the write point and the busy flag registered on the
        // previous ring clock (ICC7.Q2) is still set. A low here is a commit
        // this ring clock - the same condition the real WRITE mux-select net
        // resolves to once the CR/scroll terms (not yet wired) are inactive.
        _writeCommitGate.A1 = cursorHere;
        _writeCommitGate.B1 = _writeAckRegister.Q2;
        _writeCommitGate.C1 = _writeAckRegister.Q2;
        var writeCommitBar = _writeCommitGate.Y1;

        var commitWrite = ringClock && !writeCommitBar;
        var writeBar = !commitWrite;

        _writeMuxA.G = false;
        _writeMuxA.S = writeBar;
        _writeMuxA.A1 = ReadCharacterDataBit(0);
        _writeMuxA.B1 = _characterBits[0].Out;
        _writeMuxA.A2 = ReadCharacterDataBit(1);
        _writeMuxA.B2 = _characterBits[1].Out;
        _writeMuxA.A3 = ReadCharacterDataBit(2);
        _writeMuxA.B3 = _characterBits[2].Out;
        _writeMuxA.A4 = ReadCharacterDataBit(3);
        _writeMuxA.B4 = _characterBits[3].Out;

        _writeMuxB.G = false;
        _writeMuxB.S = writeBar;
        _writeMuxB.A1 = ReadCharacterDataBit(4);
        _writeMuxB.B1 = _characterBits[4].Out;
        _writeMuxB.A2 = ReadCharacterDataBit(5);
        _writeMuxB.B2 = _characterBits[5].Out;

        _characterBits[0].In = _writeMuxA.Y1;
        _characterBits[1].In = _writeMuxA.Y2;
        _characterBits[2].In = _writeMuxA.Y3;
        _characterBits[3].In = _writeMuxA.Y4;
        _characterBits[4].In = _writeMuxB.Y1;
        _characterBits[5].In = _writeMuxB.Y2;

        _cursorBit.In = commitWrite ? false : _cursorSetPending || cursorHere;
        _cursorSetPending = commitWrite;

        _lineBuffer.In1 = _characterBits[0].Out;
        _lineBuffer.In2 = _characterBits[1].Out;
        _lineBuffer.In3 = _characterBits[2].Out;
        _lineBuffer.In4 = _characterBits[3].Out;
        _lineBuffer.In5 = _characterBits[4].Out;
        _lineBuffer.In6 = _characterBits[5].Out;

        TickLineBufferClock();

        // ICC7 registers DA and _S1_71 on the same MEM0 edge the rings shift
        // on. Q3 (/RDA) going low is the write-acknowledge strobe; it stays
        // low for one ring clock, since committing a write clears the cursor
        // bit and so forces _S1_71 back high.
        _writeAckRegister.D2 = displayBusy;
        _writeAckRegister.D3 = writeCommitBar;

        if (ringClock)
        {
            ShiftCharacterRings();

            _writeAckRegister.Clk = false;
            _writeAckRegister.Clk = true;
        }

        // ICB3:B: a falling edge on /RDA fires the one-shot. Its Qn output
        // holds CB1 low for the pulse and releases it high at the end; that
        // rising edge is CB1's configured active transition (per WozMon's
        // CRB init), which restores CB2 high in handshake mode and so drops
        // the busy flag. CB1's IRQ1 flag also sets as a side effect, same as
        // real 6821 behaviour, but Pia.Irqb isn't wired to Cpu.Irq anywhere
        // in this system (no net for it on the schematic), so it has no
        // observable effect.
        _writeAckOneShot.ABar2 = _writeAckRegister.Q3;
        _writeAckOneShot.Tick();
        Pia.Cb1 = _writeAckOneShot.Qn2;

        // DA drives PB7 (PIA.PortB's one input-configured bit): WozMon's
        // ECHO spins while bit 7 reads 1 and writes once it reads 0.
        // Recomputed here, after the CB1 update, so the tick the one-shot
        // finishes and restores CB2 is the same tick the busy flag drops -
        // no extra character-time of lag before WozMon's next poll.
        Pia.PB = (byte)(DisplayBusy() ? 0x80 : 0x00);
    }

    // DA (ICC15:D, a 7400 wired as an inverter): NOT(CB2).
    private bool DisplayBusy()
    {
        _displayBusyInverter.A1 = Pia.Cb2;
        _displayBusyInverter.B1 = Pia.Cb2;
        return _displayBusyInverter.Y1;
    }

    private bool ReadCharacterDataBit(int index) =>
        (Pia.PortB & (1 << CharacterDataPiaBit[index])) != 0;

    // ICB2:B (7410): _S1_97 = NAND(V0,V1,V2), high except on a glyph row's
    // last scanline (V0=V1=V2=1). Feeds both the ring clock (MEM0) and the
    // 2519 line buffer's Recirculate pin.
    private bool GlyphRowLastScanlineBar()
    {
        _glyphRowLastLineGate.A1 = _lineCounterLow.Qa; // V0
        _glyphRowLastLineGate.B1 = _lineCounterLow.Qb; // V1
        _glyphRowLastLineGate.C1 = _lineCounterLow.Qc; // V2
        return _glyphRowLastLineGate.Y1;
    }

    // MEM0 = NOR(_S1_0, LINE0, _S1_97) - the ring shift clock, asserted for
    // exactly 1024 character-times per frame (see the _memClockGate field
    // remarks).
    private bool EvaluateRingClock()
    {
        // LINE0 = NAND(H6, CL); CL tied high (see _line0Gate remarks).
        _line0Gate.A1 = _horizontalCounterHigh.Qc; // H6 - 40-character window
        _line0Gate.B1 = true;                       // CL

        // _S1_0 = NOR(ICD6.Qd, /VBL).
        _memBlankingGate.A1 = _horizontalCounterLow.Qd;
        _memBlankingGate.B1 = NotVerticalBlank;

        _memClockGate.A1 = _memBlankingGate.Y1; // _S1_0
        _memClockGate.B1 = _line0Gate.Y1;       // LINE0
        _memClockGate.C1 = GlyphRowLastScanlineBar(); // _S1_97

        return _memClockGate.Y1;
    }

    // One MEM0-gated ring advance. Routed through ICC11A (DS0025) so its
    // level-shift/inversion is load-bearing: driving its inputs high-then-
    // low produces the rising edge on Phi1/Phi2 that the 2504s shift on. The
    // real chip clocks each 2504 once per phase and O3/O4 alternate at half
    // the character rate, so one MEM0-gated character-time is one ring
    // position either way - emit that single shift.
    private void ShiftCharacterRings()
    {
        _shiftClockDriver.In1 = true;
        _shiftClockDriver.In2 = true;
        SetPhase1(_shiftClockDriver.Out1);
        SetPhase2(_shiftClockDriver.Out2);

        _shiftClockDriver.In1 = false;
        _shiftClockDriver.In2 = false;
        SetPhase1(_shiftClockDriver.Out1);
        SetPhase2(_shiftClockDriver.Out2);

        _ringPosition = (_ringPosition + 1) & 1023;
    }

    // ICC3's own Recirculate(pin4)/Clock(pin6) control, confirmed from the
    // schematic: Recirculate = _S1_97 = NAND(V0,V1,V2) - active
    // (recirculate/hold) except when the scanline-within-row count is 7, so
    // the line buffer only writes fresh data during a row's *last* scanline,
    // preloading the *next* row one scanline early (avoiding the 40-clock
    // pipeline delay a same-row load would cause). Clock = net "LINE0" =
    // NAND(HBL, CL), where HBL is ICD7's own weight-4 output
    // (_horizontalCounterHigh.Qc, already modelled by the horizontal
    // counter) and CL is _lineBufferClockCounter's weight-4 output above.
    // V0-V2 come from the real ICD8 counter (_lineCounterLow) via
    // GlyphRowLastScanlineBar.
    private void TickLineBufferClock()
    {
        var hbl = _horizontalCounterHigh.Qc;

        _lineBufferClockCounter.A = false;
        _lineBufferClockCounter.B = true;
        _lineBufferClockCounter.C = false;
        _lineBufferClockCounter.D = true;
        _lineBufferClockCounter.Enp = _lineBufferClockCounter.Qd;
        _lineBufferClockCounter.Ent = _lineBufferClockCounter.Qd;
        _lineBufferClockCounter.Load = _lineBufferClockCounter.Qd;
        PulseClock(_lineBufferClockCounter);

        var cl = _lineBufferClockCounter.Qc;

        _lineBuffer.Recirculate = GlyphRowLastScanlineBar();
        _lineBuffer.Clk = !(hbl && cl);
    }

    private void SetPhase1(bool value)
    {
        foreach (var chip in _characterBits)
        {
            chip.Phi1 = value;
        }
        _cursorBit.Phi1 = value;
    }

    private void SetPhase2(bool value)
    {
        foreach (var chip in _characterBits)
        {
            chip.Phi2 = value;
        }
        _cursorBit.Phi2 = value;
    }

    // Test-only introspection (see Signetics2504Chip.Peek's own remarks) -
    // AppleISystem.Video.cs no longer needs this, since it now reads the
    // real 2519 line buffer's OUT pins instead of peeking the character
    // rings by position. logicalPosition is a ring position in the same
    // sense _ringPosition is - "whichever position was at the write point
    // on the character-time this data was (or would be) committed" - not a
    // fixed array slot: a genuinely recirculating register has no such
    // thing, since Signetics2504Chip.Phi2 physically moves every bit one
    // array slot on every clock. A bit written while _ringPosition read L is
    // at array index ((current _ringPosition) - 1 - L) mod 1024 - 0 the tick
    // right after it's written, all the way up to 1023 (Out) one full
    // rotation later, which is what makes L a stable enough concept to call
    // a "position" at all.
    internal byte PeekCharacterCodeForTests(int logicalPosition)
    {
        var arrayIndex = ((_ringPosition - 1 - logicalPosition) % 1024 + 1024) % 1024;

        byte value = 0;

        for (var i = 0; i < _characterBits.Length; i++)
        {
            if (_characterBits[i].Peek(arrayIndex))
            {
                value |= (byte)(1 << i);
            }
        }

        return value;
    }
}
