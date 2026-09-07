using Aemula.Emulation.Chips;

namespace Aemula.Emulation.Systems.AppleI;

// The terminal section's timing chain, wired gate-for-gate from
// docs/apple-i-netlist.txt (sheet 1). Everything here hangs off one
// oscillator, the 14.31818MHz crystal ZQ1 (with ICD12:B/ICD12:C as its
// inverting amplifier), which is this system's master tick.
//
//   ICC13 (74175) is clocked by that oscillator directly. Its FF1 is wired
//   D1 = /Q1, so it toggles every master tick and /Q1 (_S1_50) is the
//   7.159MHz dot clock. FF2 registers ICC15:C = NAND(HSYNC, _S1_160) and its
//   /Q2 (_S1_68) is the composite-sync level into the video mixer, so sync
//   is re-timed to the dot clock rather than taken straight off the counter
//   decode. FF3/FF4 track the cursor - see AppleISystem.CharacterMemory.cs.
//
//   ICD11 (74161) is clocked by the dot clock. CET, CEP and PE are all tied
//   to its own QD, P1/P3 float high and P0/P2 are grounded, so it self-loads
//   10 the clock after it wraps past 15 and cycles 10,11,12,13,14,15,0: a
//   7-dot period. QD is CLA, the 1.0227MHz character clock (high for six
//   dots, low for one) that clocks every counter below and paces the CPU; QC
//   is CL, high for the last four dots of each character-time; RCO marks the
//   sixth dot and, NANDed with H6 by ICD10:A, is the 74166's parallel-load
//   strobe (AppleISystem.Video.cs).
//
//   ICD6 (74160, ones digit) and ICD7 (74161, tens digit) are the horizontal
//   character counter, both clocked by CLA. ICD6's enables float high; ICD7
//   is enabled by ICD6's carry. Both reload through ICD12:F from LASTH
//   (ICD7's carry, high for the single character-time the pair reads 15/9).
//   Their preset pins are wired to ICD7's own QD (net _S1_39) and ground:
//   ICD6 loads 5, ICD7 loads 9, so a line is the 65 character-times from
//   95 to 159. HSYNC (ICC9:B) is OR(H4, H6) - low only while ICD7 reads 10,
//   i.e. character-times 100-109, a 9.8us pulse. H6 (ICD7's QC) is high for
//   ICD7 = 12..15, character-times 120-159: the 40-column active window. So
//   a line is 5 character-times of front porch, 10 of sync, 10 of back
//   porch and 40 of picture. The same _S1_39 net is ICD8/ICD9's
//   asynchronous clear, which holds the line counter at zero from power-on
//   until the horizontal counter first reaches its running range.
//
//   ICD8/ICD9 (74161s) are the line counter V0-V7, clocked by CLA, enabled
//   once per line by LASTH (ICD8's CET; ICD9 is enabled by ICD8's carry) and
//   gated by VINH on both CEPs. V0-V2 address the row within a glyph and
//   gate the character rings; ICD10:C decodes NAND(V6, V7) into /VBL, low
//   for lines 192-255. Their synchronous load is the scroll mechanism: ICC9:C
//   pulls /LOAD low while a character write commits during vertical blank,
//   and the preset pins are wired to _S1_0 (ICC10:C) except ICD9's P2,
//   which is grounded, so the counter reloads to $BF = 191 - the last active
//   line - and replays one row's worth of ring shifts.
//
//   ICD15 (74161) generates vertical sync. It is clocked by CLA, counts once
//   per line (CET = CEP = LASTH), loads 10 (P1/P3 float high) whenever
//   _S1_42 is low, and that net is a diode AND (CR1, CR2, CR3 into a TTL
//   input's own pull-up) of V5, VBL and ICC10:D = NOR(V3, V4): it is only
//   released while the line count is 224-231. From 10 it counts to 15,
//   wraps, and its QD (_S1_160) then stays low for eight line-clocks: the
//   vertical sync pulse, with no serrations - HSYNC is simply swallowed by
//   the NAND in ICC15:C. Its QB is VINH; while ICD15 is counting, VINH drops
//   for pairs of line-clocks and stalls the line counter, six times in all,
//   so the frame comes out at 262 lines rather than 256.
public sealed partial class AppleISystem
{
    private readonly Ttl74175Chip _icc13 = new();
    private readonly Ttl74161Chip _icd11 = new();

    private readonly Ttl74160Chip _icd6 = new();
    private readonly Ttl74161Chip _icd7 = new();
    private readonly Ttl74161Chip _icd8 = new();
    private readonly Ttl74161Chip _icd9 = new();
    private readonly Ttl74161Chip _icd15 = new();

    // ICD10 (7400): A = NAND(ICD11.RCO, H6), the 74166 load strobe; B =
    // LINE0 = NAND(H6, CL), the 2519 line buffer's clock; C = /VBL. Gate D
    // is on the processor sheet (/RF, DRAM refresh - not modelled, the DRAM
    // is a flat array).
    private readonly Ttl7400Chip _icd10 = new();

    // ICD12 (7404): A inverts the CR line-fill latch
    // (AppleISystem.CharacterMemory.cs); B/C are the crystal oscillator,
    // which is the master tick itself; D = VBL; E = /MEM0; F = /LASTH.
    private readonly Ttl7404Chip _icd12 = new();

    // ICC15 (7400, drawn on the processor sheet): C = NAND(HSYNC, _S1_160)
    // into ICC13's FF2; D inverts the PIA's CB2 into the DA busy flag.
    private readonly Ttl7400Chip _icc15 = new();

    private bool _lastDotClock;
    private bool _lastMem0;

    // Master ticks since the last CLA rising edge - the 6502's phi0 is high
    // for the first half of each character-time (see TickCharacterClock).
    private int _masterTicksSinceCla;

    // HSYNC (ICC9:B) and _S1_160 (ICD15's QD) are active-low nets; these
    // read true while the corresponding pulse is happening.
    public bool HSync => !HSyncBar;

    public bool VSync => !_icd15.Qd;

    // The registered composite-sync level ICC13's /Q2 feeds the mixer.
    public bool CompositeSync => !_icc13.Qn2;

    internal bool Phi0ForTests => _masterTicksSinceCla < 7;

    // How many times ICD8/ICD9 have taken their scroll reload, and the line
    // and character-time the counters read going into that CLA edge plus
    // the preset level (_S1_0) they loaded.
    internal int ScrollReloadCountForTests { get; private set; }
    internal (int Line, int CharacterTime, bool Preset) LastScrollReloadForTests { get; private set; }

    private bool HSyncBar
    {
        get
        {
            _icc9.A2 = _icd7.Qa; // H4
            _icc9.B2 = _icd7.Qc; // H6
            return _icc9.Y2;
        }
    }

    private int HorizontalCount =>
        NibbleValue(_icd7.Qa, _icd7.Qb, _icd7.Qc, _icd7.Qd) * 10 +
        NibbleValue(_icd6.Qa, _icd6.Qb, _icd6.Qc, _icd6.Qd);

    private int VerticalCount =>
        (NibbleValue(_icd9.Qa, _icd9.Qb, _icd9.Qc, _icd9.Qd) << 4) |
        NibbleValue(_icd8.Qa, _icd8.Qb, _icd8.Qc, _icd8.Qd);

    // /VBL (ICD10:C): NAND(V6, V7).
    private bool NotVerticalBlank
    {
        get
        {
            _icd10.A3 = _icd9.Qc; // V6
            _icd10.B3 = _icd9.Qd; // V7
            return _icd10.Y3;
        }
    }

    private static int NibbleValue(bool qa, bool qb, bool qc, bool qd) =>
        (qa ? 1 : 0) | (qb ? 2 : 0) | (qc ? 4 : 0) | (qd ? 8 : 0);

    private void InitializeVideoTiming()
    {
        // ICD11: P0/P2 grounded, P1/P3 floating high.
        _icd11.A = false;
        _icd11.B = true;
        _icd11.C = false;
        _icd11.D = true;

        // ICD6: CEP/CET not connected - a floating TTL input reads high.
        _icd6.Enp = true;
        _icd6.Ent = true;

        // ICD15: P0/P2 grounded, P1/P3 floating high.
        _icd15.A = false;
        _icd15.B = true;
        _icd15.C = false;
        _icd15.D = true;

        InitializePixelShiftRegister();
    }

    private void TickVideoTiming()
    {
        // ICC13 clocks on every master tick. FF1's D is its own /Q; FF2's D
        // is the NAND of HSYNC and ICD15's QD; FF3/FF4's Ds were left on the
        // pins by the last EvaluateWriteLogic.
        _icc13.D1 = _icc13.Qn1;
        _icc15.A3 = HSyncBar;
        _icc15.B3 = _icd15.Qd;
        _icc13.D2 = _icc15.Y3;
        _icc13.Clk = false;
        _icc13.Clk = true;

        var dotClock = _icc13.Qn1;
        if (dotClock && !_lastDotClock)
        {
            TickDotClock();
        }
        _lastDotClock = dotClock;

        // For the character-time after a MEM0 edge the write network is
        // re-evaluated every tick, so ICC13's FF3/FF4 (which clock at this
        // rate) and the 2519's inputs settle on the ring's new output before
        // the next edge samples them - see _writeLogicSettleTicks.
        if (_writeLogicSettleTicks > 0)
        {
            _writeLogicSettleTicks--;
            EvaluateWriteLogic();
        }

        _masterTicksSinceCla++;
        if (_masterTicksSinceCla == 7)
        {
            Cpu.Phi0 = false;
        }

        TickCompositeVideo();
    }

    // One rising edge of the 7.159MHz dot clock.
    private void TickDotClock()
    {
        // ICD1 shares this clock edge with ICD11 and takes its load strobe
        // from ICD11's pre-edge RCO, so it goes first.
        TickPixelShiftRegister();

        var claBefore = _icd11.Qd;
        PulseClock(_icd11);
        _icd11.Enp = _icd11.Qd;
        _icd11.Ent = _icd11.Qd;
        _icd11.Load = _icd11.Qd;

        if (_icd11.Qd && !claBefore)
        {
            TickCharacterClock();
        }

        // MEM0 (ICC5:A) rises when CL does, four dots before the end of an
        // active character-time on a glyph row's last scanline; that edge
        // clocks ICC7 and fires the ring shift.
        var mem0 = EvaluateRingClock();
        if (mem0 && !_lastMem0)
        {
            CommitRingClock();
        }
        _lastMem0 = mem0;

        // ICC3 (2519): Recirculate = _S1_97, Clock = LINE0 (ICD10:B), which
        // rises when CL falls at the end of every active character-time. Its
        // inputs are the write muxes' outputs, left on the pins by
        // EvaluateWriteLogic.
        _lineBuffer.Recirculate = GlyphRowLastScanlineBar();
        _lineBuffer.Clk = _icd10.Y2;
    }

    // One rising edge of CLA, the character clock: the five 74160/74161
    // counters clock together, with their load/enable pins at the levels
    // the pre-edge state left them.
    private void TickCharacterClock()
    {
        _icd7.Enp = _icd6.Rco; // H10
        _icd7.Ent = _icd6.Rco;
        var lastH = _icd7.Rco; // LASTH

        // ICD12:F: /LASTH -> both horizontal stages' /LOAD.
        _icd12.A6 = lastH;
        var horizontalLoadBar = _icd12.Y6;

        var s1_39 = _icd7.Qd;

        _icd6.Load = horizontalLoadBar;
        _icd6.A = s1_39;
        _icd6.B = false;
        _icd6.C = s1_39;
        _icd6.D = false;

        _icd7.Load = horizontalLoadBar;
        _icd7.A = s1_39;
        _icd7.B = false;
        _icd7.C = false;
        _icd7.D = s1_39;

        // ICD8/ICD9: enabled once per line by LASTH, gated by VINH, loaded
        // from _S1_0 by ICC9:C's OR(/VBL, /WC1) - _verticalCounterLoadBar,
        // the last write-network evaluation (AppleISystem.CharacterMemory.cs).
        var vinh = _icd15.Qb;
        var linePreset = EvaluateMemBlanking(); // _S1_0

        _icd8.Enp = vinh;
        _icd8.Ent = lastH;
        _icd8.Load = _verticalCounterLoadBar;
        _icd8.A = linePreset;
        _icd8.B = linePreset;
        _icd8.C = linePreset;
        _icd8.D = linePreset;

        _icd9.Enp = vinh;
        _icd9.Ent = _icd8.Rco;
        _icd9.Load = _verticalCounterLoadBar;
        _icd9.A = linePreset;
        _icd9.B = linePreset;
        _icd9.C = false; // P2 grounded: V6 never preset
        _icd9.D = linePreset;

        // ICD15: /LOAD = _S1_42, the diode AND of V5, VBL (ICD12:D) and
        // ICC10:D = NOR(V3, V4).
        _icc10.A4 = _icd8.Qd; // V3
        _icc10.B4 = _icd9.Qa; // V4
        var s1_155 = _icc10.Y4;
        _icd12.A4 = NotVerticalBlank;
        var vbl = _icd12.Y4;
        _icd15.Load = _icd9.Qb && vbl && s1_155;
        _icd15.Enp = lastH;
        _icd15.Ent = lastH;

        if (!_verticalCounterLoadBar)
        {
            ScrollReloadCountForTests++;
            LastScrollReloadForTests = (VerticalCount, HorizontalCount, linePreset);
        }

        PulseClock(_icd6);
        PulseClock(_icd7);
        PulseClock(_icd8);
        PulseClock(_icd9);
        PulseClock(_icd15);

        // The carry chain is wired, not clocked: re-present the post-edge
        // enables so ICD7's RCO (LASTH) and ICD9's RCO (LAST) read correctly
        // for the rest of the character-time - ICC7 samples both.
        _icd7.Enp = _icd6.Rco;
        _icd7.Ent = _icd6.Rco;
        _icd8.Ent = _icd7.Rco;
        _icd9.Ent = _icd8.Rco;

        // _S1_39 is also ICD8/ICD9's asynchronous /CLR.
        _icd8.Clr = _icd7.Qd;
        _icd9.Clr = _icd7.Qd;

        // The 6502's phi0 follows the character clock: the bus cycle happens
        // on the rising edge and phi0 falls half a character-time later
        // (TickVideoTiming). ICB3:A, the DRAM row/column multiplexer's
        // one-shot, also fires here on real hardware; the DRAM is a flat
        // array so it has nothing to time.
        _masterTicksSinceCla = 0;
        Cpu.Phi0 = true;
        DoCpuMemoryAccess();

        TickCharacterTimeHousekeeping();
        EvaluateWriteLogic();
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
