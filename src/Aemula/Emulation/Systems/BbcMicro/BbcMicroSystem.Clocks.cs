namespace Aemula.Emulation.Systems.BbcMicro;

public sealed partial class BbcMicroSystem
{
    // The clock logic feeds back on itself - the 1MHz synchroniser clears a
    // flip-flop whose output is the CPU's clock, which decides the PHI1 the
    // synchroniser looks at - so each tick is settled by repeating
    // EvaluatePass until nothing changes. This many passes is a safety stop;
    // a tick normally takes two or three.
    private const int MaxSettlePasses = 16;

    // How many ticks after the CPU's clock falls its address and R/W outputs
    // reach the rest of the board. The 6502's address bus changes up to
    // ~100ns after PHI1 rises and holds for at least 30ns after PHI2 falls;
    // two ticks (125ns) keep the old address stable for everything that
    // samples as the clock falls (the paged-ROM latch, the interrupt-enable
    // latch, the video ULA's registers), while it is still well settled
    // before the next PHI2 begins four ticks later.
    private const int AddressDelayTicks = 2;

    // The address and R/W as the rest of the board sees them.
    private ushort _busAddress;
    private bool _busRw;
    private int _busHoldTicks;

    // 1MHZE, the 1MHz bus clock (IC34's second flip-flop), as the devices
    // clocked by it last saw it.
    private bool _eHigh;

    // The settled state is a fixed point of EvaluatePass for the nets the pass
    // reads from outside its own flip-flops (see SettleInputs). While those are
    // unchanged another pass can do nothing, so a tick skips it. The 2MHz and
    // 8MHz clocks are left out of that key because they toggle every few ticks
    // and are only ever flip-flop clocks (and IC30's D inputs, which matter
    // only as the 8MHz clock rises): a falling edge just updates the stored
    // clock level, and a rising edge needs a pass unless it clocks IC30 into
    // the state it is already in.
    private long _settleInputs = -1;

    // The flip-flops' state as the last settle left it; nothing else changes
    // it, so the next settle starts from it.
    private int _settledSignature = int.MinValue;
    private bool _clock2MHz;
    private bool _clock8MHz;

    private void Settle()
    {
        var clock2MHz = VideoUla.Clock2MHz;
        var clock8MHz = VideoUla.Clock8MHz;

        if (SettleInputs() == _settleInputs
            && !(clock2MHz && !_clock2MHz)
            && (!(clock8MHz && !_clock8MHz) || IsIc30Steady(clock2MHz)))
        {
            if (clock2MHz != _clock2MHz)
            {
                _clock2MHz = clock2MHz;
                _ic34.Clk2 = false;
            }

            if (clock8MHz != _clock8MHz)
            {
                _clock8MHz = clock8MHz;
                _ic30.D2 = clock2MHz;
                _ic30.D1 = _ic30.Q2;
                _ic30.Clk1 = clock8MHz;
                _ic30.Clk2 = clock8MHz;
            }

            return;
        }

        _clock2MHz = clock2MHz;
        _clock8MHz = clock8MHz;

        var before = _settledSignature;

        for (var pass = 0; pass < MaxSettlePasses; pass++)
        {
            EvaluatePass();

            var after = Signature();

            if (after == before)
            {
                break;
            }

            before = after;
        }

        _settledSignature = before;
        _settleInputs = SettleInputs();
    }

    // IC30 is a two-stage shift register: on an 8MHz rising edge its second
    // flip-flop takes the 2MHz clock and its first takes the second's old
    // output. If both already hold what they would take, the edge changes
    // nothing.
    private bool IsIc30Steady(bool clock2MHz) =>
        _ic30.Q2 == clock2MHz && _ic30.Q1 == _ic30.Q2;

    // Everything EvaluatePass takes from outside the flip-flops it settles,
    // other than the 2MHz and 8MHz clocks: the CPU's clock phases and data,
    // the reset line, the address decode's inputs, the VIAs' IRQs and the
    // port B bits that drive the addressable latch.
    private long SettleInputs() =>
        (Cpu.Phi1 ? 1L : 0)
        | (Cpu.Phi2 ? 2L : 0)
        | (_resetTimer.Out ? 4L : 0)
        | (SystemVia.Irq ? 16L : 0)
        | (UserVia.Irq ? 32L : 0)
        | (_busRw ? 64L : 0)
        | ((long)(Cpu.Data & 0x0F) << 8)
        | ((long)(SystemVia.PortB & 0x0F) << 12)
        | ((long)_busAddress << 16);

    private int Signature()
    {
        var signature = 0;

        void Add(bool bit) => signature = (signature << 1) | (bit ? 1 : 0);

        Add(_ic30.Q1);
        Add(_ic30.Qn1);
        Add(_ic30.Q2);
        Add(_ic34.Q1);
        Add(_ic34.Qn1);
        Add(_ic34.Q2);
        Add(_ic31.Q1);
        Add(_ic31.Qn1);
        Add(_ic31.Q2);
        Add(_ic31.Qn2);
        Add(_ic97.Q1);
        Add(_ic76.Qa);
        Add(_ic76.Qb);
        Add(_ic76.Qc);
        Add(_ic76.Qd);
        Add(Cpu.Phi2);
        Add(_eHigh);

        return signature;
    }

    private int _peripheralKey = -1;

    private void EvaluatePeripherals(bool n130, bool phi1, bool resetBar, byte data, byte portB)
    {
        // IC76 latches the paged-ROM number from the data bus when the CPU's
        // clock falls (IC30's /Q rises) during a write to &FE30-&FE3F.
        _ic76.A = (data & 0x01) != 0;
        _ic76.B = (data & 0x02) != 0;
        _ic76.C = (data & 0x04) != 0;
        _ic76.D = (data & 0x08) != 0;
        _ic76.Load = _romselBar;
        _ic76.Clk = n130;

        // IC97: reading &FE20-&FE2F (INTON) clears the latch at the end of
        // the cycle and reading &FE18-&FE1F (INTOFF) sets it, enabling or
        // disabling the Econet NMI. Its /Q is an input to the NMI gate, IC27.
        // Its /CLR is wired to its own Q, so once clear it stays clear until
        // INTOFF sets it.
        _ic97.D1 = _intonBar;
        _ic97.Pre1 = _intoffBar;
        _ic97.Clr1 = _ic97.Q1;
        _ic97.Clk1 = phi1;

        // IC27 is an open-collector wired-OR onto /NMI. Its first gate passes
        // the Econet clock detector (IC91, whose output is low with no
        // Econet fitted), the third the 8271's INT, which is low while the
        // 8271 is absent.
        _ic27.A1 = _ic97.Qn1;
        _ic27.B1 = false;
        _ic27.A3 = false;
        _ic27.B3 = true;
        var nmiAsserted = _ic27.Y1 == false || _ic27.Y3 == false;

        Cpu.Nmi = !nmiAsserted;
        Cpu.Irq = SystemVia.Irq && UserVia.Irq;
        Cpu.Res = resetBar;
        SystemVia.Res = resetBar;
        UserVia.Res = resetBar;

        // IC32, the addressable latch, follows System VIA PB0-PB3 whenever
        // IC31's first flip-flop enables it.
        _ic32.A0 = (portB & 0x01) != 0;
        _ic32.A1 = (portB & 0x02) != 0;
        _ic32.A2 = (portB & 0x04) != 0;
        _ic32.D = (portB & 0x08) != 0;
        _ic32.G = _ic31.Qn1;
    }

    // One evaluation of the glue logic. Every flip-flop samples the nets as
    // they were when the pass began, so two flip-flops clocked by the same
    // edge (IC30's pair, which form a shift register) see each other's old
    // outputs; a flip-flop clocked by another's output sees that change on the
    // next pass instead.
    private void EvaluatePass()
    {
        var phi1 = Cpu.Phi1;

        // The reset circuit: IC33 inverts the 555's output into RST#.
        _ic33.A2 = _resetTimer.Out;
        var resetBar = _ic33.Y2;

        DecodeBus(phi1);

        var c2 = VideoUla.Clock2MHz;
        var clock8MHz = VideoUla.Clock8MHz;

        // Net names are the netlist's: _N203 is IC30's CPU-clock flip-flop,
        // _N204 the other half of the CPU's clock gate, _N130 the paged-ROM
        // latch's clock.
        var e = _ic34.Q2;
        var eBar = _ic34.Qn2;
        var n203 = _ic30.Q1;
        var n130 = _ic30.Qn1;
        var ic30Q2 = _ic30.Q2;
        var n204 = _ic34.Qn1;
        var n190 = _ic31.Qn2;

        // IC33's first inverter turns "a 1MHz device is selected" around.
        _ic33.A1 = _oneMHzDeviceSelected;
        var n173 = _ic33.Y1;

        // IC28 clears IC30's CPU-clock flip-flop while the CPU is waiting in
        // PHI1 for a 1MHz-bus cycle to come round: 1MHZE, PHI1 and a 1MHz
        // device selected, all high.
        _ic28.A1 = e;
        _ic28.B1 = phi1;
        _ic28.C1 = _oneMHzDeviceSelected;
        var n168 = _ic28.Y1;

        _ic29.A3 = n190;
        _ic29.B3 = n173;
        var n188 = _ic29.Y3;

        // The synchroniser. IC30: the 2MHz clock delayed by two 8MHz clocks
        // (so the CPU's clock follows it by 3/16 of a microsecond) is what
        // drives the CPU, unless IC28 holds it cleared. IC34's second
        // flip-flop divides 2MHz by two into 1MHZE; its first is preset by
        // reset and otherwise follows IC31.
        _ic30.Clr1 = n168;
        _ic34.Pre1 = resetBar;
        _ic31.Clr1 = _viaABar;
        _ic31.Pre2 = n204;

        _ic30.D2 = c2;
        _ic30.Clk2 = clock8MHz;
        _ic30.D1 = ic30Q2;
        _ic30.Clk1 = clock8MHz;

        _ic34.D2 = eBar;
        _ic34.Clk2 = c2;
        _ic34.D1 = n188;
        _ic34.Clk1 = n203;

        _ic31.D1 = true;
        _ic31.Clk1 = eBar;
        _ic31.D2 = n173;
        _ic31.Clk2 = e;

        // The latches, interrupt wiring and addressable latch below depend on
        // only these nets, so they are re-evaluated only when one changes.
        var data = Cpu.Data;
        var portB = SystemVia.PortB;
        var sysIrq = SystemVia.Irq;
        var peripheralKey = (n130 ? 0x1 : 0)
            | (phi1 ? 0x2 : 0)
            | (_romselBar ? 0x4 : 0)
            | (_intonBar ? 0x8 : 0)
            | (_intoffBar ? 0x10 : 0)
            | (_ic97.Q1 ? 0x20 : 0)
            | (_ic31.Qn1 ? 0x40 : 0)
            | (sysIrq ? 0x80 : 0)
            | (UserVia.Irq ? 0x100 : 0)
            | (resetBar ? 0x200 : 0)
            | ((data & 0x0F) << 10)
            | ((portB & 0x0F) << 14);

        if (peripheralKey != _peripheralKey)
        {
            _peripheralKey = peripheralKey;
            EvaluatePeripherals(n130, phi1, resetBar, data, portB);
        }

        // The CPU's clock input is IC29's fourth OR gate.
        _ic29.A4 = _ic30.Q1;
        _ic29.B4 = _ic34.Qn1;
        var cpuClock = _ic29.Y4;

        if (cpuClock != Cpu.Phi2)
        {
            if (cpuClock)
            {
                OnCpuPhi2Rising();
            }
            else
            {
                OnCpuPhi2Falling();
            }
        }

        var eNow = _ic34.Q2;
        if (eNow != _eHigh)
        {
            _eHigh = eNow;

            if (eNow)
            {
                OnOneMHzEnableRising();
            }
            else
            {
                OnOneMHzEnableFalling();
            }
        }
    }
}
