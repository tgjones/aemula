using Aemula.Emulation.Chips;

namespace Aemula.Emulation.Systems.SpaceInvaders;

// Phase 2 of docs/space-invaders-television-plan.md: the horizontal/vertical
// sync chain and the CPU interrupt trigger it drives. Modelled from the
// documented behavior in MAME's mw8080bw.cpp/.h driver header and
// cross-checked against the MiSTer Arcade-SpaceInvaders_MiSTer core's
// rtl/mw8080.vhd (see the plan doc's "Hardware reference" section) - the
// board schematic obtained this session confirmed a four-74161-plus-7474
// topology for this chain (functionally the same "D5/E5/E6/E7/A5/E3" roles
// MAME's Midway-board comments describe) but its exact chip designators on
// this session's Taito-board scan weren't legible with confidence, so the
// fields below are named descriptively rather than by (possibly wrong)
// silkscreen reference - confirm and rename against the schematic if/when
// it's re-read at higher resolution.
public sealed partial class SpaceInvadersSystem
{
    // Horizontal counter: an 8-bit binary counter (low nibble + high
    // nibble) that free-runs 0..255 during the visible portion of a
    // scanline, then - synchronously reloaded to 192 the instant it hits
    // its own terminal count - counts 192..255 again (64 more states)
    // during HBLANK, for 320 pixel-clock states per line total.
    private readonly Ttl74161Chip _hCounterLow; // H0-H3 (1H,2H,4H,8H)
    private readonly Ttl74161Chip _hCounterHigh; // H4-H7 (16H,32H,64H,128H)

    // Vertical counter: same shape as the horizontal pair, but only ticks
    // once per scanline (see TickVideoTiming's vClockEnable) and reloads to
    // 0xDA (not 0) on its own terminal count, free-running 0x20..0xFF
    // visible then 0xDA..0xFF during VBLANK - 262 lines/frame total.
    private readonly Ttl74161Chip _vCounterLow; // V0-V3 (1V,2V,4V,8V)
    private readonly Ttl74161Chip _vCounterHigh; // V4-V7 (16V,32V,64V,128V)

    // HBLANK (half 1) and VBLANK (half 2): each toggles on its own
    // counter's terminal count, and its output feeds directly back into
    // that same counter's high-nibble reload data - see TickVideoTiming.
    private readonly Ttl7474Chip _blankingFlipFlops;

    // The interrupt-trigger latch: continuously resampled every pixel
    // clock from (!64V & 128V) | VBLANK, so its low-to-high *transition*
    // (the only thing that matters - see TickInterrupt) happens exactly
    // when the vertical count reaches 0x80 (VBLANK low - mid-screen,
    // RST 1) or 0xDA (VBLANK high - VBLANK start, RST 2). Real hardware
    // clears this flip-flop from the CPU's memory-access control signals
    // (per MAME's driver comment); this model doesn't need to reproduce
    // that explicitly, since the D-expression's own window naturally falls
    // low well before the next trigger point either way (V=0xC0 and
    // V=0x20 respectively) - what's actually latched until CPU
    // acknowledgment is _cpu.Int itself (a separate flag, set from this
    // flip-flop's rising edge below, cleared in ReadCpuBus's
    // StatusWordInterruptAcknowledge case), not this flip-flop's own Q1.
    private readonly Ttl7474Chip _interruptFlipFlop;

    private bool _lastInterruptFlipFlopQ1;

    public bool Hblank => _blankingFlipFlops.Q1;
    public bool Vblank => _blankingFlipFlops.Q2;

    /// <summary>
    /// The raw H/V counter state, for tests.
    /// </summary>
    internal (byte H, byte V) GetVideoScannerStateForTests()
    {
        var h = (byte)(
            (_hCounterHigh.Qd ? 1 << 7 : 0) |
            (_hCounterHigh.Qc ? 1 << 6 : 0) |
            (_hCounterHigh.Qb ? 1 << 5 : 0) |
            (_hCounterHigh.Qa ? 1 << 4 : 0) |
            (_hCounterLow.Qd ? 1 << 3 : 0) |
            (_hCounterLow.Qc ? 1 << 2 : 0) |
            (_hCounterLow.Qb ? 1 << 1 : 0) |
            (_hCounterLow.Qa ? 1 << 0 : 0));

        var v = (byte)(
            (_vCounterHigh.Qd ? 1 << 7 : 0) |
            (_vCounterHigh.Qc ? 1 << 6 : 0) |
            (_vCounterHigh.Qb ? 1 << 5 : 0) |
            (_vCounterHigh.Qa ? 1 << 4 : 0) |
            (_vCounterLow.Qd ? 1 << 3 : 0) |
            (_vCounterLow.Qc ? 1 << 2 : 0) |
            (_vCounterLow.Qb ? 1 << 1 : 0) |
            (_vCounterLow.Qa ? 1 << 0 : 0));

        return (h, v);
    }

    internal byte GetNextInterruptForTests() => _nextInterrupt;

    private void TickVideoTiming()
    {
        // Every fourth master tick is a pixel-clock edge (master 19.968MHz
        // / 4 = 4.992MHz pixel clock) - see SpaceInvadersSystem.CyclesPerSecond.
        if (_masterClock % 4 != 0)
        {
            return;
        }

        // Pre-edge reads: what the horizontal chain's terminal count and
        // HBLANK's *next* value (its D-input, which is its own Q-bar) are
        // right now, before anything below is pulsed this tick. Wiring the
        // reload data below to hblankNext (rather than the flip-flop's
        // current, pre-toggle Q) is what keeps the counter's reload value
        // and the flip-flop's new state in agreement after they're pulsed
        // together on the same edge.
        //
        // Checked directly against both chips' Q outputs, rather than via
        // _hCounterHigh.Rco (Ent && Qa&&Qb&&Qc&&Qd) - Rco's Ent is only
        // fresh *after* it's reassigned a few lines below, so reading it
        // here would see last tick's leftover Ent instead of this tick's.
        var hTerminalCount = IsAtMax(_hCounterLow) && IsAtMax(_hCounterHigh);
        var hblankNext = _blankingFlipFlops.Qn1;

        _hCounterLow.Enp = true;
        _hCounterLow.Ent = true;
        _hCounterLow.Load = !hTerminalCount;
        _hCounterLow.A = false;
        _hCounterLow.B = false;
        _hCounterLow.C = false;
        _hCounterLow.D = false;

        _hCounterHigh.Enp = true;
        _hCounterHigh.Ent = _hCounterLow.Rco;
        _hCounterHigh.Load = !hTerminalCount;
        _hCounterHigh.A = false;
        _hCounterHigh.B = false;
        // 192 (the HBLANK reload target) and 0 (the visible-region reload
        // target) differ only in these top two bits - both are 1 for 192,
        // both 0 for 0 - so both data pins are simply HBLANK's own next
        // value.
        _hCounterHigh.C = hblankNext;
        _hCounterHigh.D = hblankNext;

        // The vertical chain only advances once per scanline: at the
        // *second* horizontal terminal count of the line (the one ending
        // HBLANK, wrapping H back to 0), not the first (the one starting
        // HBLANK). HBLANK reads high, pre-edge, for exactly the second one.
        var vClockEnable = _blankingFlipFlops.Q1 && hTerminalCount;

        PulseCounter(_hCounterLow);
        PulseCounter(_hCounterHigh);

        if (hTerminalCount)
        {
            _blankingFlipFlops.D1 = hblankNext;
            PulseFlipFlop1(_blankingFlipFlops);
        }

        if (vClockEnable)
        {
            var vTerminalCount = IsAtMax(_vCounterLow) && IsAtMax(_vCounterHigh);
            var vblankNext = _blankingFlipFlops.Qn2;

            _vCounterLow.Enp = true;
            _vCounterLow.Ent = true;
            _vCounterLow.Load = !vTerminalCount;
            // 0xDA (11011010) and 0x20 (00100000) don't share a clean
            // split like the horizontal pair's 192/0 do - each bit below
            // is individually either tied to VBLANK's next value or held
            // low, whichever reproduces both target bytes.
            _vCounterLow.A = false; // bit 0: 0 both ways
            _vCounterLow.B = vblankNext; // bit 1: 0xDA has it set, 0x20 doesn't
            _vCounterLow.C = false; // bit 2: 0 both ways
            _vCounterLow.D = vblankNext; // bit 3: 0xDA has it set, 0x20 doesn't

            _vCounterHigh.Enp = true;
            _vCounterHigh.Ent = _vCounterLow.Rco;
            _vCounterHigh.Load = !vTerminalCount;
            _vCounterHigh.A = vblankNext; // bit 4: 0xDA has it set, 0x20 doesn't
            _vCounterHigh.B = !vblankNext; // bit 5: 0x20 has it set, 0xDA doesn't
            _vCounterHigh.C = vblankNext; // bit 6: 0xDA has it set, 0x20 doesn't
            _vCounterHigh.D = vblankNext; // bit 7: 0xDA has it set, 0x20 doesn't

            PulseCounter(_vCounterLow);
            PulseCounter(_vCounterHigh);

            if (vTerminalCount)
            {
                _blankingFlipFlops.D2 = vblankNext;
                PulseFlipFlop2(_blankingFlipFlops);

                if (vblankNext)
                {
                    // Entering VBLANK - one whole frame's worth of active
                    // video has just been scanned. Until Phase 4 replaces
                    // this with a real per-pixel shift-register draw, blit
                    // the frame here instead of on a magic pixel-clock
                    // count.
                    UpdateDisplay();
                }
            }
        }

        TickInterrupt();
    }

    private void TickInterrupt()
    {
        var v6 = _vCounterHigh.Qc; // 64V
        var v7 = _vCounterHigh.Qd; // 128V

        _interruptFlipFlop.D1 = (!v6 && v7) || Vblank;
        PulseFlipFlop1(_interruptFlipFlop);

        var q1 = _interruptFlipFlop.Q1;

        if (q1 && !_lastInterruptFlipFlopQ1)
        {
            // 0xC7 | (64V << 4) | (!64V << 3): RST 1 (0xCF) when 64V is low
            // (V=0x80), RST 2 (0xD7) when 64V is high (V=0xDA).
            _nextInterrupt = (byte)(0xC7 | (v6 ? 0x10 : 0) | (v6 ? 0 : 0x08));
            _cpu.Int = true;
        }

        _lastInterruptFlipFlopQ1 = q1;
    }

    private static bool IsAtMax(Ttl74161Chip chip) => chip.Qa && chip.Qb && chip.Qc && chip.Qd;

    private static void PulseCounter(Ttl74161Chip chip)
    {
        chip.Clk = false;
        chip.Clk = true;
    }

    private static void PulseFlipFlop1(Ttl7474Chip chip)
    {
        chip.Clk1 = false;
        chip.Clk1 = true;
    }

    private static void PulseFlipFlop2(Ttl7474Chip chip)
    {
        chip.Clk2 = false;
        chip.Clk2 = true;
    }
}
