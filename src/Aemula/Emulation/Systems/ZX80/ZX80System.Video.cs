using Aemula.Emulation.Chips;

namespace Aemula.Emulation.Systems.ZX80;

public sealed partial class ZX80System
{
    // IC5: captures the character byte the CPU reads out of the display
    // file during the non-refresh (T1) half of the M1 that's walking the
    // screen memory, and holds it steady through the refresh half that
    // follows, so the ROM address mux below has a stable character code to
    // work with - see TickVideo. Channel 6 isn't part of the character code
    // at all: it latches a synchronized copy of the NOP-decode signal for
    // the shift-register load logic to read back a cycle later, the same
    // way the real board's channel 4 does (chosen here because channels
    // 0/1/2/3/5/7 are already the six character-code bits plus the
    // inverse-video flag - channel 6 is this model's equivalent of the real
    // board's one genuinely spare channel, D6 never being wired to the
    // latch at all).
    private readonly Ttl74373Chip _characterLatch;

    // IC6/IC7/IC8: three quad muxes select, per ROM address bit, between
    // the CPU's own address (normal fetch) and the video-generated address
    // (refresh half of a display-file M1) - see GetRomAddress. IC6 only
    // uses one of its four channels for this (A8); its other three feed the
    // ROM's chip-select and buffered A14/A15, which the netlist shows as
    // dangling on the video-mode side - deferred to the keyboard-matrix
    // work in phase 4, since the diode chain those channels also touch is
    // the only other consumer.
    private readonly Ttl74157Chip _romAddressMuxHigh;
    private readonly Ttl74157Chip _romAddressMuxMid;
    private readonly Ttl74157Chip _romAddressMuxLow;

    // IC9: shifts the character ROM's bitmap byte out one bit per pixel.
    // SH/LD (pin 1) is driven straight off data bus line D4 - the same
    // signal the NOP generator is busy forcing low or releasing - so
    // loading and shifting share the one control line for free (see
    // TickVideo). The clock (pin 2) is an RC-differentiated edge off PHI2X
    // on the real board (C9 into R2); this chip's own model already only
    // reacts to the transition, not the pulse width, so it's wired straight
    // to PHI2X undifferentiated - unlike the SH/LD strobe below, a wider
    // pulse here can't cause a wrong shift/load decision.
    private readonly Ttl74165Chip _videoShiftRegister;

    // IC21: only the divide-by-8 ripple section is wired up on this board -
    // QA (pin 12) and CKA (pin 14) are unconnected - giving a plain 3-bit
    // counter for the 0-7 scanline position within a character row. It's
    // clocked by the sync flip-flop chain's own output and held in reset
    // while a keyboard row is being read (see TickVideo).
    private readonly Ttl7493Chip _scanlineCounter;

    // IC11/IC12: four NAND gates apiece doing two unrelated jobs, both
    // wired in TickVideo. IC11 gates 1-2 and IC12 gates 1-2 cross-couple
    // into a latch that XORs the character code's inverse-video flag into
    // the composite VIDEO bit IC20 gate 2 produces for phase 4. IC11 gates
    // 3-4 form a second latch, self-contained within IC11, that resets the
    // scanline counter for as long as a keyboard row read is in progress -
    // released again by the ROM's own OUT instruction, the same one-shot-
    // via-software trick the sync chain below uses. IC12's other two gates
    // feed the ROM's chip-select mux channel, dangling on its video-mode
    // input in this netlist and deferred to phase 4 along with it.
    private readonly Ttl7400Chip _ic11;
    private readonly Ttl7400Chip _ic12;

    // IC13: three of its six inverters are used here (two more are already
    // doing unrelated work as ZX80System.cs's _addressDecodeInverters -
    // this is a second, real IC13). Gate 2 turns the LE-decode NAND below
    // back to a positive-sense latch enable; gate 3 produces NOT(D6) for
    // that same decode; gate 4 feeds the inverse-video latch above. Two
    // more gates exist on the real board but feed only the keyboard-diode/
    // CS1 corner deferred to phase 4, so they're not modelled.
    private readonly Ttl7404Chip _ic13;

    // IC15: two of its six open-collector inverters have no part in the
    // NOP-forcing bank the plan describes (that bank is folded into a
    // single Cpu.Data = 0x00 in DoCpuMemoryAccess, since which of eight
    // near-identical gates forces which bit makes no observable difference
    // - the real board's IC14 has one gate, presumably forcing D1, whose
    // output isn't captured at all in this netlist). Gate 1 here turns the
    // NOP-release condition into the shared wired-OR node the forcing bank
    // reads; gate 4 turns /M1 into the level that clocks the sync flip-flop
    // chain, once per M1 cycle.
    private readonly Ttl7405Chip _ic15;

    // IC16/IC17: NAND/OR glue tying the pieces above together - see
    // TickVideo for what each gate does; there's no single label that
    // covers either chip as a whole.
    private readonly Ttl7410Chip _ic16;
    private readonly Ttl7432Chip _ic17;

    // IC19: the horizontal sync pulse itself. Both flip-flops clock
    // together off the same /M1-derived level as the shift-register load
    // strobe; flip-flop 2's output feeds flip-flop 1's D so the pulse is
    // shaped to a fixed width in M1-cycle units rather than raw clock
    // cycles, and flip-flop 1's own Q *is* the SYNC signal - it clocks the
    // scanline counter directly and clears IC18's other flip-flop, which
    // the ROM's own code kicks back out of reset with a single OUT
    // instruction (see TickVideo).
    private readonly Ttl7474Chip _ic19;

    // Edge-detects PHI for the RC differentiator on IC11 pin 2 (C11 into
    // R25): real hardware turns each PHI rising edge into a ~20ns spike,
    // narrow enough that the NAND gates it feeds see a strobe rather than a
    // level. That distinction matters here - unlike the shift register's
    // own clock above, this pulse is ANDed together with held levels to
    // open a brief window, and a signal that stayed high for a whole PHI
    // half-period instead of a brief instant would hold that window open
    // far longer than the real board does. Modelled as exactly one
    // Tick()-wide pulse on the edge instead.
    private bool _previousPhi;

    /// <summary>
    /// The ROM's real address (0-4095): A9-A11 are wired straight from the
    /// CPU's own address pins with no mux at all (the Z80's I register,
    /// fixed at 0x0E by the ROM's own startup code, already puts the right
    /// constant there during refresh, so there's nothing to switch). A0-A8
    /// go through IC6/7/8, selecting the CPU's address normally or, during
    /// the refresh half of a display-file M1, the character code just
    /// latched by IC5 combined with the scanline counter - exactly the
    /// address the real character ROM was built against (confirmed against
    /// MAME's zx_v.cpp, which indexes its character generator with
    /// <c>(code &amp; 0x3F) &lt;&lt; 3 | scanline</c>).
    /// </summary>
    private int GetRomAddress(ushort address)
    {
        // /RFSH wires straight to the mux select pin with no inverter in
        // between: Cpu.Rfsh true (deasserted, T1/T2 of a normal fetch)
        // already selects the B (CPU-address) side, per Ttl74157Chip's own
        // S ? B : A.
        var normalFetch = Cpu.Rfsh;

        _romAddressMuxLow.S = normalFetch;
        _romAddressMuxLow.G = false;
        _romAddressMuxLow.A1 = _scanlineCounter.QB;
        _romAddressMuxLow.B1 = (address & 0x01) != 0;
        _romAddressMuxLow.A2 = _scanlineCounter.QC;
        _romAddressMuxLow.B2 = (address & 0x02) != 0;
        _romAddressMuxLow.A3 = _scanlineCounter.QD;
        _romAddressMuxLow.B3 = (address & 0x04) != 0;
        _romAddressMuxLow.A4 = _characterLatch.Q0 ?? false;
        _romAddressMuxLow.B4 = (address & 0x08) != 0;

        _romAddressMuxMid.S = normalFetch;
        _romAddressMuxMid.G = false;
        _romAddressMuxMid.A1 = _characterLatch.Q1 ?? false;
        _romAddressMuxMid.B1 = (address & 0x10) != 0;
        _romAddressMuxMid.A2 = _characterLatch.Q2 ?? false;
        _romAddressMuxMid.B2 = (address & 0x20) != 0;
        _romAddressMuxMid.A3 = _characterLatch.Q3 ?? false;
        _romAddressMuxMid.B3 = (address & 0x40) != 0;
        _romAddressMuxMid.A4 = _characterLatch.Q4 ?? false;
        _romAddressMuxMid.B4 = (address & 0x80) != 0;

        _romAddressMuxHigh.S = normalFetch;
        _romAddressMuxHigh.G = false;
        _romAddressMuxHigh.A1 = _characterLatch.Q5 ?? false;
        _romAddressMuxHigh.B1 = (address & 0x100) != 0;

        var lowBits =
            (_romAddressMuxLow.Y1 ? 0x001 : 0) |
            (_romAddressMuxLow.Y2 ? 0x002 : 0) |
            (_romAddressMuxLow.Y3 ? 0x004 : 0) |
            (_romAddressMuxLow.Y4 ? 0x008 : 0) |
            (_romAddressMuxMid.Y1 ? 0x010 : 0) |
            (_romAddressMuxMid.Y2 ? 0x020 : 0) |
            (_romAddressMuxMid.Y3 ? 0x040 : 0) |
            (_romAddressMuxMid.Y4 ? 0x080 : 0) |
            (_romAddressMuxHigh.Y1 ? 0x100 : 0);

        return lowBits | (address & 0x0E00);
    }

    /// <summary>
    /// IC14/IC15's open-collector data-bus force. Called from
    /// DoCpuMemoryAccess (after the real ROM/RAM byte for this address is
    /// already on Cpu.Data) during the non-refresh half of every M1 fetch:
    /// forces the whole byte to 0x00 unless the CPU is already halted,
    /// address bit 15 is clear (real code, not the display-file mirror), or
    /// bit 6 of the real byte is set - the newline character deliberately
    /// shares its value with the Z80's own HALT opcode (0x76), so letting
    /// bit-6-set bytes through for real execution is exactly what ends a
    /// scanline.
    /// </summary>
    private void TickNopGenerator()
    {
        var nopDecode = GetNopDecodeSignal();

        _ic17.A3 = nopDecode;
        _ic17.B3 = Cpu.M1;
        var release = _ic17.Y3;

        _ic15.A1 = release;
        var forceNop = _ic15.Y1 is null; // floating (pulled high by R3) = forced.

        if (forceNop)
        {
            Cpu.Data = 0x00;
        }
    }

    // IC16 gate 2: NAND(/HALT, A15, NOT(D6)) - false exactly when this M1's
    // real byte should be forced to a NOP. Shared between the live decode
    // TickNopGenerator acts on and the latched copy IC5 channel 6 captures
    // for the shift-register load logic to read back a cycle later.
    private bool GetNopDecodeSignal()
    {
        _ic13.A3 = (Cpu.Data & 0x40) != 0; // D6, off the real byte just read.
        var notD6 = _ic13.Y3;

        _ic16.A2 = Cpu.Halt;
        _ic16.B2 = (Cpu.Address & 0x8000) != 0;
        _ic16.C2 = notD6;
        return _ic16.Y2;
    }

    // IC17 gates 1+4: OR(/RD, /IORQ) NAND'd... rather, De Morgan'd through a
    // second OR with A0, giving an active-low "an I/O read of an even port
    // is in progress" signal from plain OR gates - the standard trick of
    // building an active-low AND-of-lows out of an OR gate. Shared between
    // the horizontal counter-reset latch above (which only needs to know a
    // keyboard read happened) and the keyboard matrix read in
    // ZX80System.Keyboard.cs (which needs it to know when to drive Cpu.Data
    // at all).
    private bool GetKbdSignal()
    {
        _ic17.A1 = Cpu.Rd;
        _ic17.B1 = Cpu.IoRq;
        var ioReadOrIoRq = _ic17.Y1;

        _ic17.A4 = ioReadOrIoRq;
        _ic17.B4 = (Cpu.Address & 0x01) != 0;
        return _ic17.Y4;
    }

    private void TickVideo(bool phi2X)
    {
        _videoShiftRegister.Clk = phi2X;

        // /INT is wired straight to A6 - no gate in between. During the
        // refresh half of a display-file M1, A6 is bit 6 of the Z80's own
        // R register (I sits fixed in the top nibble), so R's own auto-
        // increment is what wakes the CPU out of HALT once per scanline,
        // "abuse of the Z80's refresh cycle" in the fullest sense.
        Cpu.Int = (Cpu.Address & 0x40) != 0;

        var phi = _ic18.Q2;
        var notPhi = _ic18.Qn2;
        var mreqAsserted = !Cpu.MReq;

        // IC5's latch enable: transparent exactly while a real (non-
        // refresh) memory cycle has valid data on the bus, gated to PHI's
        // low half - IC16 gate 1 NANDs /RFSH, /PHI and MREQ-asserted
        // together and IC13 gate 2 inverts it back to a positive enable.
        _ic16.A1 = Cpu.Rfsh;
        _ic16.B1 = notPhi;
        _ic16.C1 = mreqAsserted;
        _ic13.A2 = _ic16.Y1; // IC13 gate 2 is the real inverter - feed it the NAND's raw output.
        var le = _ic13.Y2;

        var nopDecode = GetNopDecodeSignal();

        _characterLatch.Oe = false; // OE tied to 0V - always enabled.
        _characterLatch.D0 = (Cpu.Data & 0x01) != 0;
        _characterLatch.D1 = (Cpu.Data & 0x02) != 0;
        _characterLatch.D2 = (Cpu.Data & 0x04) != 0;
        _characterLatch.D3 = (Cpu.Data & 0x08) != 0;
        _characterLatch.D4 = (Cpu.Data & 0x10) != 0;
        _characterLatch.D5 = (Cpu.Data & 0x20) != 0;
        _characterLatch.D6 = nopDecode;
        _characterLatch.D7 = (Cpu.Data & 0x80) != 0; // the inverse-video flag.
        _characterLatch.Le = le;

        // Shift-register load vs. shift, off data bus line D4 (see the
        // field comment on _videoShiftRegister). IC16 gate 3 NANDs: a
        // strobe on every PHI rising edge (the real RC differentiator on
        // IC11 pin 2, modelled as a one-tick pulse - see _previousPhi), the
        // *previous* cycle's latched NOP-decode result, and MREQ asserted.
        // When all three land together, D4 - and so SH/LD - is pulled low
        // for this instant, loading the ROM's just-addressed bitmap byte
        // into the shifter instead of shifting it.
        var phiRisingEdge = phi && !_previousPhi;
        _previousPhi = phi;

        _ic13.A5 = _characterLatch.Q6 ?? false; // last cycle's latched NOP decode.
        var notLatchedNopDecode = _ic13.Y5;

        _ic16.A3 = phiRisingEdge;
        _ic16.B3 = notLatchedNopDecode;
        _ic16.C3 = Cpu.MReq; // this gate takes raw /MREQ, unlike gate 1's active-high input above.
        var shLd = _ic16.Y3;

        _videoShiftRegister.ShLd = shLd;

        // The parallel inputs are the character ROM's bitmap byte,
        // DoCpuMemoryAccess having already placed it on Cpu.Data via
        // GetRomAddress during this same refresh cycle. A (LSB) through H
        // (MSB) load straight off D0-D7, matching the real board's own
        // wiring, and the class's own shift order (H out first, A last)
        // shifts pixels out MSB-first as a font bitmap expects.
        _videoShiftRegister.A = (Cpu.Data & 0x01) != 0;
        _videoShiftRegister.B = (Cpu.Data & 0x02) != 0;
        _videoShiftRegister.C = (Cpu.Data & 0x04) != 0;
        _videoShiftRegister.D = (Cpu.Data & 0x08) != 0;
        _videoShiftRegister.E = (Cpu.Data & 0x10) != 0;
        _videoShiftRegister.F = (Cpu.Data & 0x20) != 0;
        _videoShiftRegister.G = (Cpu.Data & 0x40) != 0;
        _videoShiftRegister.H = (Cpu.Data & 0x80) != 0;

        // The inverse-video latch: IC11 gates 1-2 feed a cross-coupled pair
        // on IC12 (gates 1-2), set by a strobe on the same PHI edge used
        // above and reset by the character code's own inverse-video flag
        // (via IC13 gate 4 and R24 in series - a plain wire for this
        // model's purposes). Qbar is the composite VIDEO bit's second XOR
        // input, wired up once phase 4 needs it.
        _ic11.A1 = Cpu.MReq; // raw /MREQ, unlike IC16 gate 1's active-high input above.
        _ic11.B1 = phiRisingEdge;
        var setBar = _ic11.Y1;

        _ic13.A4 = shLd;
        var notShLd = _ic13.Y4;
        _ic11.A2 = _characterLatch.Q7 ?? false; // the inverse-video flag.
        _ic11.B2 = notShLd;
        var resetBar = _ic11.Y2;

        _ic12.A1 = _ic12.Y2;
        _ic12.B1 = setBar;
        _ic12.A2 = _ic12.Y1;
        _ic12.B2 = resetBar;

        // The horizontal counter-reset latch: self-contained within IC11's
        // other two gates, set by an OUT instruction's /WR+/IORQ pulse and
        // cleared by a keyboard row read's /RD+/IORQ+A0 pulse - the same
        // "software releases a hardware latch" trick the sync chain below
        // uses, just gating the scanline counter instead of the sync pulse.
        var kbd = GetKbdSignal();

        _ic17.A2 = Cpu.Wr;
        _ic17.B2 = Cpu.IoRq;
        var ioWriteOrIoRq = _ic17.Y2;

        _ic11.A3 = ioWriteOrIoRq;
        _ic11.B3 = _ic11.Y4;
        var counterResetSet = _ic11.Y3;
        _ic11.A4 = counterResetSet;
        _ic11.B4 = kbd;
        var counterReset = _ic11.Y4;

        _scanlineCounter.R01 = counterReset;
        _scanlineCounter.R02 = counterReset;

        // The sync pulse chain: IC19's two flip-flops clock together off
        // IC15 gate 4's open-collector read of /M1 (pulled up by R18) -
        // floating, so effectively high, for the whole M1 cycle, giving one
        // rising edge exactly when M1 starts.
        _ic15.A4 = Cpu.M1;
        var m1Level = _ic15.Y4 ?? true;

        _ic19.D2 = _ic18.Qn1;
        _ic19.Pre1 = true;
        _ic19.Pre2 = true;
        _ic19.Clr2 = true;
        _ic19.Clr1 = counterResetSet;
        _ic19.D1 = _ic19.Q2;
        _ic19.Clk1 = m1Level;
        _ic19.Clk2 = m1Level;

        // SYNC (IC19's own Q1) clocks the scanline counter directly.
        _scanlineCounter.B = _ic19.Q1;

        // IC18's flip-flop 1: a pure async latch (CLK1 tied to +5V, so it
        // never clocks) set by /IORQ and cleared by SYNC. The ROM's video
        // driver routine presets it once, near start-of-frame, with a
        // single OUT instruction; the sync chain above tears it back down
        // every time SYNC pulses, self-limiting the pulse width.
        _ic18.D1 = false;
        _ic18.Clk1 = true;
        _ic18.Pre1 = Cpu.IoRq;
        _ic18.Clr1 = _ic19.Q1;
    }

    /// <summary>SYNC (IC19 pin 5) - the horizontal sync pulse itself.</summary>
    internal bool SyncSignalForTest => _ic19.Q1;

    /// <summary>The 0-7 scanline position within a character row (IC21's QD/QC/QB).</summary>
    internal int ScanlineForTest =>
        (_scanlineCounter.QD ? 4 : 0) | (_scanlineCounter.QC ? 2 : 0) | (_scanlineCounter.QB ? 1 : 0);
}
