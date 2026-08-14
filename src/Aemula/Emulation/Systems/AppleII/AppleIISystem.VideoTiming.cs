using Aemula.Emulation.Chips;

namespace Aemula.Emulation.Systems.AppleII;

// Phase 3: the video scanner (horizontal/vertical counter chain) and the
// clock generator that derives PHASE0 (the 6502's clock) from it, including
// the once-per-scanline "long cycle" stretch. Modelled from Jim Sather's
// "Understanding the Apple II", chapter 3 ("Timing Generation and the Video
// Scanner"), cross-checked against AppleWin's independently Sather-cited
// video scanner implementation.
public sealed partial class AppleIISystem
{
    // The video scanner: one 16-bit counter built from four cascaded
    // 74LS161s (D11-D14 in Sather's schematic), split into a 7-bit
    // horizontal section (H0-H5 plus HPE') and a 9-bit vertical section
    // (VA-VC plus V0-V5). D13 holds H4, H5, HPE', and VA together - one of
    // the "four and a quarter" chips Sather's text calls out.
    private readonly Ttl74161Chip _videoScannerD14; // H0, H1, H2, H3
    private readonly Ttl74161Chip _videoScannerD13; // H4, H5, HPE', VA
    private readonly Ttl74161Chip _videoScannerD12; // VB, VC, V0, V1
    private readonly Ttl74161Chip _videoScannerD11; // V2, V3, V4, V5

    // The 2MHz sequencer (Sather's "C2"): shifts RAS'/AX/CAS'/Q3 through a
    // 74S195, normally cycling every 7 master ticks. Feeds PHASE0 generation
    // below. RAS'/AX'/CAS' themselves aren't consumed until the RAM phase,
    // but Q3 and AX (Qd/Qb) are needed here.
    private readonly Ttl74S195Chip _clockSequencer;

    // PHASE0 generation (Sather's B1 + C1): two of B1's four flip-flops
    // implement a two-stage pipeline - the first computes the mux equation,
    // the second (one 14M tick later) is PHASE0 itself - fed by one half of
    // a 74LS153 selecting among {B1-Q2, PHASE0, PHASE0'} by {Q3, AX}.
    private readonly Ttl74175Chip _phase0FlipFlops;
    private readonly Ttl74153Chip _phase0Mux;

    // Sequencing state for the 2MHz sequencer: position 0-5 shift, position
    // 6 synchronously reloads to 0000, closing a 7-tick cycle. This mirrors
    // Sather's documented "four clock / three clock" split (Q3 low for 4
    // ticks, high for 3) - the literal J/K' feedback net that produces this
    // from a free-running Johnson counter (which would otherwise cycle
    // through 8 states, not 7) isn't recoverable from the available
    // schematic scan, so the 7th-tick reload is used to enforce the
    // documented period exactly.
    private byte _clockSequencerIndex;
    private bool _isLongCycle;
    private byte _longCyclePauseTicksRemaining;

    // PHASE0's period spans two of the sequencer's 7-tick cycles (14 ticks
    // normally). Only the first of that pair is eligible to carry the long
    // cycle's stretch - otherwise a single scanline's HPE' assertion (which
    // holds steady for the whole PHASE0 period) would stretch both.
    private bool _isFirstClockSequencerCycleOfPhase0 = true;

    private bool _lastPhase0;

    public bool Phase0 => _phase0FlipFlops.Q4;

    // HBL (UTAIIe:8-10,F8.5), derived from the horizontal state bits.
    public bool Hbl => !H5 && (!H4 || !H3);

    // VBL' = (v4 & v3)' (UTAIIe:5-10,#3), so VBL is the plain AND.
    public bool Vbl => V4 && V3;

    // The color burst gate: ~9 cycles of COLOR REFERENCE go out during this
    // window, once per scanline, starting right after HSync.
    public bool ColorBurstGate => !H5 && !H4 && H3 && H2;

    private bool H0 => _videoScannerD14.Qa;
    private bool H1 => _videoScannerD14.Qb;
    private bool H2 => _videoScannerD14.Qc;
    private bool H3 => _videoScannerD14.Qd;
    private bool H4 => _videoScannerD13.Qa;
    private bool H5 => _videoScannerD13.Qb;

    // HPE' (Horizontal Preset Enable), active low: asserted for exactly one
    // of the 65 horizontal states.
    private bool HpeBar => _videoScannerD13.Qc;

    private bool V2 => _videoScannerD11.Qa;
    private bool V3 => _videoScannerD11.Qb;
    private bool V4 => _videoScannerD11.Qc;

    /// <summary>
    /// The raw video scanner state, formatted to match the binary strings
    /// Sather's text uses: H as HPE',H5,H4,H3,H2,H1,H0 (7 bits); V as
    /// V5,V4,V3,V2,V1,V0,VC,VB,VA (9 bits).
    /// </summary>
    internal (byte H, ushort V) GetVideoScannerStateForTests()
    {
        var h = (byte)(
            (HpeBar ? 1 << 6 : 0) |
            (H5 ? 1 << 5 : 0) |
            (H4 ? 1 << 4 : 0) |
            (H3 ? 1 << 3 : 0) |
            (H2 ? 1 << 2 : 0) |
            (H1 ? 1 << 1 : 0) |
            (H0 ? 1 << 0 : 0));

        var v = (ushort)(
            (_videoScannerD11.Qd ? 1 << 8 : 0) | // V5
            (V4 ? 1 << 7 : 0) |
            (V3 ? 1 << 6 : 0) |
            (V2 ? 1 << 5 : 0) |
            (_videoScannerD12.Qd ? 1 << 4 : 0) | // V1
            (_videoScannerD12.Qc ? 1 << 3 : 0) | // V0
            (_videoScannerD12.Qb ? 1 << 2 : 0) | // VC
            (_videoScannerD12.Qa ? 1 << 1 : 0) | // VB
            (_videoScannerD13.Qd ? 1 << 0 : 0)); // VA

        return (h, v);
    }

    private void TickVideoTiming()
    {
        TickClockSequencer();
        TickPhase0Generator();

        var phase0RisingEdge = Phase0 && !_lastPhase0;
        _lastPhase0 = Phase0;

        if (phase0RisingEdge)
        {
            // LDPS' occurs toward the end of PHASE0; approximated here as
            // coincident with PHASE0's rising edge.
            TickVideoScanner();
        }

        Cpu.Phi0 = Phase0;

        if (phase0RisingEdge)
        {
            DoCpuMemoryAccess();
        }
    }

    private void TickClockSequencer()
    {
        if (_longCyclePauseTicksRemaining > 0)
        {
            _longCyclePauseTicksRemaining--;
            return;
        }

        if (_clockSequencerIndex == 0 && _isFirstClockSequencerCycleOfPhase0)
        {
            // Every 65th cycle, while HPE' is low, generation of the 2MHz
            // signals is delayed for one half a COLOR REFERENCE period
            // (two 14M ticks) - the "long cycle".
            _isLongCycle = !HpeBar;
        }

        if (_clockSequencerIndex < 6)
        {
            var serial = !_clockSequencer.Qd; // NOT(Q3).

            _clockSequencer.ShLd = true;
            _clockSequencer.J = serial;
            _clockSequencer.Kn = !serial;
            PulseClockSequencer();

            _clockSequencerIndex++;

            if (_clockSequencerIndex == 6 && _isFirstClockSequencerCycleOfPhase0 && _isLongCycle)
            {
                _longCyclePauseTicksRemaining = 2;
            }
        }
        else
        {
            _clockSequencer.ShLd = false;
            _clockSequencer.A = false;
            _clockSequencer.B = false;
            _clockSequencer.C = false;
            _clockSequencer.D = false;
            PulseClockSequencer();

            _clockSequencerIndex = 0;
            _isFirstClockSequencerCycleOfPhase0 = !_isFirstClockSequencerCycleOfPhase0;
        }
    }

    private void PulseClockSequencer()
    {
        _clockSequencer.Clk = false;
        _clockSequencer.Clk = true;
    }

    private void TickPhase0Generator()
    {
        // C1 develops AX' . Q2 + AX . (PHASE0 . Q3 + Q3' . PHASE0'), the
        // D-input to B1-Q2 (here, _phase0FlipFlops' D3/Q3). PHASE0 itself
        // (D4/Q4) follows B1-Q2 by one further 14M tick.
        _phase0Mux.A = _clockSequencer.Qd; // Q3
        _phase0Mux.B = _clockSequencer.Qb; // AX
        _phase0Mux.C1_0 = _phase0FlipFlops.Q3;
        _phase0Mux.C1_1 = _phase0FlipFlops.Q3;
        _phase0Mux.C1_2 = !_phase0FlipFlops.Q4;
        _phase0Mux.C1_3 = _phase0FlipFlops.Q4;
        _phase0Mux.G1 = false;

        _phase0FlipFlops.D3 = _phase0Mux.Y1;
        _phase0FlipFlops.D4 = _phase0FlipFlops.Q3;

        _phase0FlipFlops.Clk = false;
        _phase0FlipFlops.Clk = true;
    }

    private void TickVideoScanner()
    {
        _videoScannerD14.Enp = true;
        // Pauses for the same one tick D13 spends reloading (below), so H0-H3
        // stay frozen through the horizontal section's repeated zero state
        // rather than ticking over to 0001 underneath it.
        _videoScannerD14.Ent = HpeBar;
        _videoScannerD14.Load = true;

        _videoScannerD13.Enp = true;
        _videoScannerD13.Ent = _videoScannerD14.Rco;
        // HPE' asserted (low) synchronously reloads H4, H5 to 0 and HPE'
        // back to 1; VA holds via self-feedback on its data input.
        _videoScannerD13.Load = HpeBar;
        _videoScannerD13.A = false;
        _videoScannerD13.B = false;
        _videoScannerD13.C = true;
        _videoScannerD13.D = _videoScannerD13.Qd;

        _videoScannerD12.Enp = true;
        _videoScannerD12.Ent = _videoScannerD13.Rco;

        _videoScannerD11.Enp = true;
        _videoScannerD11.Ent = _videoScannerD12.Rco;

        // Both vertical chips share "VERTICAL PRESET'", asserted when the
        // full 9-bit vertical section is at its terminal count (all ones),
        // reloading to the Eurapple-less NTSC preset 011111010.
        var verticalPresetBar = !_videoScannerD11.Rco;
        _videoScannerD12.Load = verticalPresetBar;
        _videoScannerD12.A = true; // VB
        _videoScannerD12.B = false; // VC
        _videoScannerD12.C = true; // V0
        _videoScannerD12.D = true; // V1

        _videoScannerD11.Load = verticalPresetBar;
        _videoScannerD11.A = true; // V2
        _videoScannerD11.B = true; // V3
        _videoScannerD11.C = true; // V4
        _videoScannerD11.D = false; // V5

        PulseCounter(_videoScannerD14);
        PulseCounter(_videoScannerD13);
        PulseCounter(_videoScannerD12);
        PulseCounter(_videoScannerD11);
    }

    private static void PulseCounter(Ttl74161Chip chip)
    {
        chip.Clk = false;
        chip.Clk = true;
    }
}
