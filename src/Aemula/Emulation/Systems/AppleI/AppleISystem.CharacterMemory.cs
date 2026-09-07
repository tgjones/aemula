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
// The cursor and carriage-return handling is also gate-level. The cursor is
// a single 0 travelling in a field of 1s around ICC11B (the seventh 2504).
// ICC13 (74175) FF4 registers ICC11B.OUT and brings out CURS = /Q4, so CURS
// is high exactly when the cursor 0 is at the write point, one dot-clock
// behind the ring itself. ICC13 FF3 registers _S1_96 (ICC14's spare 2:1
// channel: CURS while idle, forced low by a write or by CLR) and its /Q3
// feeds ICC12:C, whose other input is /WC2; that AND is ICC11B's IN. With
// /WC2 high the cursor just recirculates; a committed write drops /WC2 for
// one ring clock, which drops the old cursor and lets the FF3 path re-seat
// a fresh one one place further on - Chris Espinosa's "flip-flops 2 and 3
// at C13 re-set the cursor bit on the next character clock" (quoted at
// https://www.righto.com/2022/04/inside-apple-1s-shift-register-memory.html).
// /WC2 is ICC7's own D6->Q6, one MEM0 clock behind /WC1 (ICC12:B).
//
// A carriage return ($0D) is decoded by ICC6:C -> ICC5:C -> ICC8:B and
// latches _S1_85 (via ICD12:A), which ICC7 FF4 holds as _S1_88 every ring
// clock until end of line. While it holds, diode CR4 wire-ORs it onto the
// CLEAR SCREEN net (B4-12), forcing CLR high so the write muxes inject $00
// into every column the beam passes - the CR is never itself stored. At end
// of line ICC8:A drops _S1_169, /WC1 pulses low, /WC2 follows, the cursor is
// re-seated at the start of the next line and the latch self-clears.
//
// The cursor flash is a free-running 555 (ICD13) gated onto that one cell.
// ICC12:A ANDs the 555 output with _S1_96 (high only while the cursor sits
// idle at the write point) to make _S1_139; ICC10:A NORs _S1_139 with the
// RD7 bit-plane's data to make _S1_110, which is that plane's own 2519
// line-buffer input. While _S1_139 is low _S1_110 just passes the plane bit
// through inverted; while it is high it forces that code bit, so the cursor
// cell alternates between '@' and blank at the 555 rate. The inversion is
// undone at the character generator's address pin (AppleISystem.Video.cs) -
// on real hardware the 2513's mask has that one bit flipped to match this
// wiring; the shared ROM image here doesn't, so the compensation is explicit.
//
// One reading taken on trust: the extracted netlist puts ICC14's spare
// channel's two data pins (CURS, GND) the opposite way round from what's
// used here. As drawn it would feed the cursor ring a constant 1 while idle
// and never recirculate the cursor at all, so the channel is treated as
// GND-on-write / CURS-on-idle.
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

    // ICC11B - the seventh 2504, carrying a single circulating cursor marker
    // (a 0 in a field of 1s) rather than a character-code bit-plane.
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

    // ICC10 (7402, quad 2-input NOR). Gate C: _S1_0 = NOR(ICD6.Qd, /VBL) -
    // during active video /VBL is high so _S1_0 is 0 and doesn't gate MEM0;
    // during vertical blank it follows !ICD6.Qd, which trims the blanked-row
    // contribution to 8 character-times per line. Gate A: _S1_110 =
    // NOR(_S1_139, RD7-plane data) - the RD7 bit-plane's own 2519 line-buffer
    // input, carrying the cursor-blink modulation (see the cursor-blink wiring
    // in TickCharacterMemory).
    private readonly Ttl7402Chip _icc10 = new();

    // ICD13 - the free-running 555 that flashes the cursor. Wired as a
    // standard astable off R10 (10k), R11 (10k) and C7 (22uF): Out high for
    // 0.693*(R10+R11)*C7, low for 0.693*R11*C7 - about a 2.2Hz square-ish
    // wave. Sampled once per character-time (the rate TickCharacterMemory
    // runs at), so the on/off times are those seconds scaled by that clock.
    private const double CharacterClockHz = 14_318_180.0 / 14.0;
    private static readonly uint CursorBlinkHighTicks =
        (uint)(0.693 * (10_000.0 + 10_000.0) * 22e-6 * CharacterClockHz);
    private static readonly uint CursorBlinkLowTicks =
        (uint)(0.693 * 10_000.0 * 22e-6 * CharacterClockHz);
    private readonly Ne555Chip _cursorBlinkOscillator = new()
    {
        PulseTicks = CursorBlinkHighTicks,
        LowTicks = CursorBlinkLowTicks,
    };

    // ICC5 (7427, triple 3-input NOR). Gate A: MEM0 = NOR(_S1_0, LINE0,
    // _S1_97), the ring shift clock. Gate B: _S1_89 = NOR(RD7, RD6, _S1_71).
    // Gate C: _S1_153 = NOR(RD2, RD5, _S1_198), the CR-family decode.
    private readonly Ttl7427Chip _icc5 = new();

    // ICC6 (7410, triple 3-input NAND). Gate B: _S1_71 = NAND(CURS,
    // _S1_148) - low exactly when the cursor is at the write point and the
    // busy flag registered on the previous ring clock is still set, i.e. a
    // character is committing into the rings this ring clock. Gate C:
    // _S1_198 = NAND(RD1, RD3, RD4).
    private readonly Ttl7410Chip _icc6 = new();

    // ICC8 (7450, dual 2-wide 2-input AND-OR-INVERT). Gate A: _S1_169 =
    // !((B4-12 . _S1_101) + (_S1_105 . _S1_85)) - drops at end of line (or
    // end of frame via the clear-screen path) while the CR latch is up.
    // Gate B: _S1_108 = !((_S1_89 . _S1_153) + (/WC2 . _S1_88)) - inverted
    // by ICD12:A into _S1_85, the CR line-fill latch: first term sets it
    // when a CR commits, second term self-holds it until /WC2 drops.
    private readonly Ttl7450Chip _icc8 = new();

    // ICD12:A (7404): _S1_85 = NOT(_S1_108).
    private readonly Ttl7404Chip _crInverter = new();

    // ICC9 (7432, quad 2-input OR). Gate A: CLR = OR(VBL, B4-12) - forces
    // the write muxes to inject $00. Gate C: _S1_41 = OR(/VBL, /WC1), the
    // ICD8/ICD9 vertical counter's active-low parallel-load control - see
    // _verticalCounterLoadBar. Gate D: /WRITE = OR(_S1_89, _S1_71),
    // active-low, low exactly when a printable char (bit 5 or 6 set) is
    // committing at the cursor; drives the write-mux selects and ICC12:B.
    private readonly Ttl7432Chip _icc9 = new();

    // ICC12 (7408, quad 2-input AND). Gate B: /WC1 = AND(/WRITE, _S1_169).
    // Gate C: _S1_93 = AND(_S1_162, /WC2) - ICC11B's recirculate/write IN.
    private readonly Ttl7408Chip _icc12 = new();

    // ICC13 (74175, quad D flip-flop, clocked at the dot rate on real
    // hardware). FF3: D3 = _S1_96, /Q3 = _S1_162. FF4: D4 = ICC11B.OUT,
    // /Q4 = CURS. Stepped once per character-time here (its D inputs only
    // move at that granularity), giving the deliberate one-clock lag
    // between the cursor ring and every CURS/_S1_162 consumer.
    private readonly Ttl74175Chip _icc13 = new();

    // ICC4 (four channels, driving the first four character bit-planes) and
    // ICC14 (two of its four channels, driving the remaining two - a third
    // is the cursor-select channel feeding _S1_96, reproduced inline below).
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
    // rings shift on:
    //   D1->Q1 = LAST  -> _S1_101   (end of frame, registered)
    //   D2->Q2 = DA    -> _S1_148   (busy flag, one ring clock late)
    //   D3->Q3 = _S1_71-> /RDA      (write-acknowledge strobe)
    //   D4->Q4 = _S1_85-> _S1_88    (CR line-fill latch, registered)
    //   D5->Q5 = LASTH -> _S1_105   (end of line, registered)
    //   D6->Q6 = /WC1  -> /WC2      (kill cursor now, re-seat one clock on)
    private readonly Ttl74174Chip _writeAckRegister = new();

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

    // B4.12 on the keyboard connector - the dedicated CLEAR SCREEN key.
    // Only reachable headlessly once the phase-4f console harness lands; a
    // running CR line-fill drives the same net through diode CR4 regardless.
    private bool _clearScreenKeyDown;

    // ICC9:C output _S1_41, the ICD8/ICD9 vertical counter's active-low
    // synchronous parallel-load pin. Low (load) only while a write is
    // committing (/WC1 low) with the beam in vertical blank (/VBL low);
    // that reload is the vertical-scroll mechanism (see TickCounters in
    // AppleISystem.VideoTiming.cs). Computed at the end of
    // TickCharacterMemory and consumed by the next TickCounters, so the
    // real one-character-time of pipeline delay between ICC12:B and the
    // CLA edge that acts on it is preserved - see the scroll notes there.
    private bool _verticalCounterLoadBar = true;

    // Bookkeeping only - not extra hardware state. The real chips only ever
    // know "the bit at Out right now"; this just names which of the 1024
    // ring positions that bit is, so AppleISystem.Video.cs's simplified
    // direct-index read (see that file) can find the same character a write
    // here just landed on. Advanced only on a real MEM0-gated shift (see
    // ShiftCharacterRings), so it completes exactly one rotation per frame.
    private int _ringPosition;

    internal bool CursorOutForTests => !_cursorBit.Out;
    internal int RingPositionForTests => _ringPosition;
    internal bool CursorBlinkOnForTests => _cursorBlinkOscillator.Out;

    // A pure recirculating register has no power-on state of its own to
    // fall back on (see Signetics2504Chip.Poke's remarks) - real hardware
    // must have some reset-time path that seeds the cursor ring with a
    // single 0 in an otherwise all-1s field, or CURS could never become
    // true and nothing could ever be typed. Seeded at Out so it reads as
    // row 0, column 0 on the very next character-time.
    private void ResetCharacterMemory()
    {
        foreach (var chip in _characterBits)
        {
            chip.Clear();
        }

        _cursorBit.Fill();
        _cursorBit.Poke(1023, false);

        _clearScreenKeyDown = false;
        _verticalCounterLoadBar = true;
        _ringPosition = 0;

        // Kick the free-running cursor 555 into its output-high phase, the
        // level a real astable settles to first from a discharged timing cap.
        _cursorBlinkOscillator.TriggerBar = true;
        _cursorBlinkOscillator.TriggerBar = false;

        // ICC7: /RDA (Q3) and /WC2 (Q6) idle high; the rest idle low.
        _writeAckRegister.D3 = true;
        _writeAckRegister.D6 = true;
        _writeAckRegister.Clk = false;
        _writeAckRegister.Clk = true;
    }

    private void TickCharacterMemory()
    {
        // MEM0 (ICC5:A) - the ring shift clock. The write-mux select and the
        // ring recirculation are the same clocked event on real hardware, so
        // a pending character can only be committed on a character-time the
        // rings are actually shifting.
        var ringClock = EvaluateRingClock();

        // DA (ICC15:D) - the live busy level, the inverse of the PIA's CB2
        // handshake line.
        var displayBusy = DisplayBusy();

        // ICC13 FF4 (D4 = ICC11B.OUT, /Q4 = CURS). Real hardware clocks
        // ICC13 at the dot rate, so within one character-time it settles;
        // step it to that fixed point here rather than carrying a
        // character-time of lag it doesn't physically have.
        _icc13.D4 = _cursorBit.Out;
        _icc13.Clk = false;
        _icc13.Clk = true;
        var curs = _icc13.Qn4;      // CURS - cursor 0 is at the write point

        var da2 = _writeAckRegister.Q2;    // _S1_148 - DA one ring clock back
        var wc2 = _writeAckRegister.Q6;    // /WC2
        var s1_88 = _writeAckRegister.Q4;  // CR line-fill latch, registered
        var s1_101 = _writeAckRegister.Q1; // LAST  registered (end of frame)
        var s1_105 = _writeAckRegister.Q5; // LASTH registered (end of line)

        // ICC6:B  _S1_71 = NAND(CURS, _S1_148).
        _icc6.A2 = curs;
        _icc6.B2 = da2;
        _icc6.C2 = da2;
        var s1_71 = _icc6.Y2;

        // ICC6:C  _S1_198 = NAND(RD1, RD3, RD4).
        _icc6.A3 = Rd(1);
        _icc6.B3 = Rd(3);
        _icc6.C3 = Rd(4);
        var s1_198 = _icc6.Y3;

        // ICC5:C  _S1_153 = NOR(RD2, RD5, _S1_198) - high only for the
        // RD1=RD3=RD4=1, RD2=RD5=0 byte family ($0D CR, $2D, $4D, $6D).
        _icc5.A3 = Rd(2);
        _icc5.B3 = Rd(5);
        _icc5.C3 = s1_198;
        var s1_153 = _icc5.Y3;

        // ICC5:B  _S1_89 = NOR(RD7, RD6, _S1_71) - high only when a control
        // byte (bits 5 and 6 clear) is committing at the cursor.
        _icc5.A2 = Rd(7);
        _icc5.B2 = Rd(6);
        _icc5.C2 = s1_71;
        var s1_89 = _icc5.Y2;

        // ICC9:D  /WRITE = OR(_S1_89, _S1_71). Active-low: 0 exactly when a
        // printable char is committing at the cursor. For a CR it stays high
        // (s1_89 = 1), so no character write happens and the mux keeps
        // recirculating - the CR byte is never stored.
        _icc9.A4 = s1_89;
        _icc9.B4 = s1_71;
        var writeBar = _icc9.Y4;

        // ICC8:B + ICD12:A  _S1_85 = (_S1_89 . _S1_153) + (/WC2 . _S1_88).
        _icc8.A2 = s1_89;
        _icc8.B2 = s1_153;
        _icc8.C2 = wc2;
        _icc8.D2 = s1_88;
        _crInverter.A1 = _icc8.Y2;
        var s1_85 = _crInverter.Y1;

        // B4-12: the CLEAR SCREEN key, wire-ORed with _S1_85 through diode
        // CR4 so a running CR line-fill drives the same clear machinery.
        var b4_12 = _clearScreenKeyDown || s1_85;

        // ICC8:A  _S1_169 = !((B4-12 . _S1_101) + (_S1_105 . _S1_85)).
        _icc8.A1 = b4_12;
        _icc8.B1 = s1_101;
        _icc8.C1 = s1_105;
        _icc8.D1 = s1_85;
        var s1_169 = _icc8.Y1;

        // ICC12:B  /WC1 = AND(/WRITE, _S1_169). Low on a committed printable
        // write, and again for the end-of-line pulse that closes a CR fill.
        _icc12.A2 = writeBar;
        _icc12.B2 = s1_169;
        var wc1 = _icc12.Y2;

        // ICC9:C  _S1_41 = OR(/VBL, /WC1) - the ICD8/ICD9 vertical counter's
        // active-low synchronous load. Both inputs active-low, so it goes
        // low (load the preset) only when a write commits while the beam is
        // in vertical blank - i.e. when the cursor has been pushed past the
        // last visible row. The reload lands the counter on one more
        // MEM0-active line, adding 40 ring shifts to the frame, which slides
        // the whole display up by one row. Latched here for the next
        // TickCounters call to act on.
        _icc9.A3 = NotVerticalBlank;
        _icc9.B3 = wc1;
        _verticalCounterLoadBar = _icc9.Y3;

        // ICC9:A  CLR = OR(VBL, B4-12) - forces $00 into the write path.
        _icc9.A1 = !NotVerticalBlank;
        _icc9.B1 = b4_12;
        var clr = _icc9.Y1;

        // ICC4 / ICC14 write mux: select low (a printable char is
        // committing) passes PIA display data to each 2504's IN; select high
        // recirculates each 2504's own OUT. CLR on the output-disable pins
        // forces IN to 0 (character code $00 -> '@') regardless.
        _writeMuxA.G = clr;
        _writeMuxA.S = writeBar;
        _writeMuxA.A1 = ReadCharacterDataBit(0);
        _writeMuxA.B1 = _characterBits[0].Out;
        _writeMuxA.A2 = ReadCharacterDataBit(1);
        _writeMuxA.B2 = _characterBits[1].Out;
        _writeMuxA.A3 = ReadCharacterDataBit(2);
        _writeMuxA.B3 = _characterBits[2].Out;
        _writeMuxA.A4 = ReadCharacterDataBit(3);
        _writeMuxA.B4 = _characterBits[3].Out;

        _writeMuxB.G = clr;
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

        // ICC14's spare 2:1 channel: _S1_96 = CURS while idle (/WRITE high),
        // GND during a write, forced to GND by CLR. (See the file header on
        // the data-pin order taken here.) ICC13 FF3 registers it; settled to
        // its fixed point the same way FF4 was above, /Q3 = _S1_162.
        var s1_96 = !clr && writeBar && curs;
        _icc13.D3 = s1_96;
        _icc13.Clk = false;
        _icc13.Clk = true;
        var s1_162 = _icc13.Qn3;

        // ICC12:C  _S1_93 = AND(_S1_162, /WC2) - ICC11B's IN. /WC2 high:
        // plain recirculation. /WC2 low (the post-write / end-of-CR pulse):
        // 0s in, dropping the old cursor so the ICC13 FF3 path re-seats a
        // fresh one one place on.
        _icc12.A3 = s1_162;
        _icc12.B3 = wc2;
        _cursorBit.In = _icc12.Y3;

        _lineBuffer.In1 = _characterBits[0].Out;
        _lineBuffer.In2 = _characterBits[1].Out;
        _lineBuffer.In3 = _characterBits[2].Out;
        _lineBuffer.In4 = _characterBits[3].Out;
        _lineBuffer.In5 = _characterBits[4].Out;

        // ICD13 (555) -> ICC12:A  _S1_139 = AND(555 out, _S1_96). _S1_96 is
        // set only while the cursor sits idle at the write point, so the
        // blink can never touch any cell but that one.
        _icc12.A1 = _cursorBlinkOscillator.Out;
        _icc12.B1 = s1_96;
        var s1_139 = _icc12.Y1;

        // ICC10:A  _S1_110 = NOR(_S1_139, RD7-plane data) - the RD7 plane's
        // own 2519 line-buffer input. With the blink term low this is just
        // the plane bit inverted (undone at the character generator - see the
        // file header); with it high the code bit is forced, flashing the
        // cursor cell between '@' and blank.
        _icc10.A2 = s1_139;
        _icc10.B2 = _characterBits[5].Out;
        _lineBuffer.In6 = _icc10.Y2;

        TickLineBufferClock();

        // ICC7 registers everything on the MEM0 edge the rings shift on.
        _writeAckRegister.D1 = _endOfFrame;
        _writeAckRegister.D2 = displayBusy;
        _writeAckRegister.D3 = s1_71;
        _writeAckRegister.D4 = s1_85;
        _writeAckRegister.D5 = _endOfLine;
        _writeAckRegister.D6 = wc1;

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

        // ICD13 free-runs off its own RC network - one character-time per
        // call, the granularity the blink is sampled at anyway.
        _cursorBlinkOscillator.Tick();

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

    // RD1-RD7 = PIA PB0-PB6 (ICA4 pins 10-16).
    private bool Rd(int number) => (Pia.PortB & (1 << (number - 1))) != 0;

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
    // exactly 1024 character-times per frame (see the _icc5 field remarks).
    private bool EvaluateRingClock()
    {
        // LINE0 = NAND(H6, CL); CL tied high (see _line0Gate remarks).
        _line0Gate.A1 = _horizontalCounterHigh.Qc; // H6 - 40-character window
        _line0Gate.B1 = true;                       // CL

        _icc5.A1 = EvaluateMemBlanking();      // _S1_0
        _icc5.B1 = _line0Gate.Y1;              // LINE0
        _icc5.C1 = GlyphRowLastScanlineBar();  // _S1_97

        return _icc5.Y1;
    }

    // ICC10:C (7402): _S1_0 = NOR(ICD6.Qd, /VBL). One of the three MEM0
    // terms - during active video /VBL holds it low and it does nothing;
    // during blank it follows !ICD6.Qd, admitting a ring shift only on the
    // ones-digit-8/9 columns, which is what trims the blanked lines'
    // contribution to 64 shifts per frame. The same net is also ICD8/ICD9's
    // preset value (their P0-P3, except ICD9's V6 which is grounded), so a
    // scroll reload fired while the beam sits in blank - where !ICD6.Qd is
    // high by the time the horizontal counter has stepped off that column -
    // loads $BF into the V0-V7 counter.
    private bool EvaluateMemBlanking()
    {
        _icc10.A1 = _horizontalCounterLow.Qd;
        _icc10.B1 = NotVerticalBlank;
        return _icc10.Y1;
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

    // Test-only: seed the cursor marker (the lone 0 in ICC11B's field of 1s)
    // so it next reaches the write point when the ring is at logical
    // position L - i.e. park the cursor at a chosen screen cell without
    // typing the tens of thousands of characters it would take to walk it
    // there through WozMon.
    internal void SetCursorLogicalPositionForTests(int logicalPosition)
    {
        var arrayIndex = ((_ringPosition - 1 - logicalPosition) % 1024 + 1024) % 1024;

        _cursorBit.Fill();
        _cursorBit.Poke(arrayIndex, false);
    }
}
