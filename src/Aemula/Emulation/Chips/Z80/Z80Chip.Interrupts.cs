namespace Aemula.Emulation.Chips.Z80;

// The /NMI, /INT, HALT-wake and /BUSRQ state machine.
//
// Recognition point (UM0080 interrupt-timing diagrams): both /NMI and /INT are
// acted on only at an instruction boundary - the transition into an opcode
// fetch with no prefix escape in force and no acknowledge sequence already
// running. /NMI is edge-triggered: a high->low transition on the pin is latched
// at any time and cleared when the acknowledge starts, so a line held low does
// not re-trigger. /INT is level-sensitive: its level sampled on the final CLK
// falling edge of the instruction is what counts, and it is taken only when
// IFF1 is set and the instruction that just retired was not EI or DI (the
// one-instruction shadow - UM0080: "the interrupt is not accepted until after
// the instruction following EI").
//
// Acknowledge sequences and their T-state counts (UM0080):
//
//   NMI  - a 5-T M1-like cycle (/M1 low, the I:R refresh and R's 7-bit
//          increment, no PC increment, fetched byte discarded), then IFF1 is
//          copied to IFF2 and IFF1 cleared, then PC is pushed (two 3-T memory
//          writes), then PC = 0x0066. 5 + 3 + 3 = 11 T-states. RETN copies
//          IFF2 back into IFF1.
//   INT  - an interrupt-acknowledge cycle: /M1 low with /IORQ (not /MREQ) low
//          and two automatic wait states, so the device has time to jam a byte
//          on the bus. IFF1 and IFF2 are both cleared. Then, by mode:
//            IM 0  the jammed byte is executed as an instruction; only the
//                  common RST case is modelled (bus 0xFF -> RST 38h), so the
//                  ack is followed by the RST push. 6 + 1 + 3 + 3 = 13 T.
//            IM 1  the bus is ignored; RST 38h is forced. 6 + 1 + 3 + 3 = 13 T.
//                  WZ = 0x0038.
//            IM 2  the jammed byte is the low half of a pointer (I << 8) | byte;
//                  PC is pushed, then the two-byte handler address is read from
//                  that pointer and jumped to. 6 + 1 + 3 + 3 + 3 + 3 = 19 T.
//                  WZ = the handler address. (The real part forces the pointer
//                  LSB even; the plain I:byte pointer is modelled here.)
//
// A HALT (0x76) parks the CPU re-fetching 0x76 with PC frozen and /HALT low.
// When an interrupt is recognised at one of those spin-M1 boundaries the halt
// clears, /HALT goes high and PC steps once past the HALT byte, so the pushed
// return address is the instruction after it.
//
// /BUSRQ is sampled at each machine-cycle boundary; when low the CPU drives
// /BUSAK low, releases the address/data/control outputs and freezes until the
// pin is released. It is not sampled between the M-cycles of an instruction -
// the next-boundary model is sufficient for a stand-alone core.

public sealed partial class Z80Chip
{
    private enum InterruptSequence : byte
    {
        None,
        Nmi,
        Int,
    }

    // Which acknowledge sequence, if any, is currently running in place of an
    // opcode. While this is not None, HandleInstruction routes to
    // HandleInterruptSequence and the normal decode tables are bypassed.
    private InterruptSequence _interruptSequence;

    // /NMI pin backing store. The pin is edge-triggered, so the setter latches
    // the high->low transition into _nmiPending the instant it happens rather
    // than sampling a level on a clock edge.
    private bool _nmiPin = true;
    private bool _nmiPending;

    // Set when EI or DI retires; consumed at the very next instruction boundary
    // to inhibit the /INT sample there exactly once. One flag is enough for the
    // one-instruction shadow: a further EI/DI just refreshes it, and a DD/FD
    // escape chain is not a boundary (its _prefix is non-None) so the whole
    // "EI; DD 09" pair is still covered.
    private bool _eiShadowPending;

    // /INT level latched on every CLK falling edge; at an instruction boundary
    // this holds the sample from the retiring instruction's final falling edge.
    private bool _intSampledLow;

    // True while /BUSRQ has been granted: /BUSAK is low, the buses are floated
    // and the state machine is frozen until /BUSRQ releases.
    private bool _busGranted;

    /// <summary>
    /// True once the CPU has parked in response to /BUSRQ - /BUSAK is low and
    /// the address, data and /MREQ //IORQ //RD //WR outputs are released.
    /// </summary>
    internal bool BusReleased => _busGranted;

    // Called at an instruction boundary (opcode fetch, no prefix, no sequence
    // running). Starts an acknowledge sequence if /NMI is latched or /INT is
    // asserted and allowed. /NMI wins when both are pending.
    private void BeginInterruptIfPending(bool intInhibited)
    {
        if (_nmiPending)
        {
            _nmiPending = false;
            WakeFromHaltForInterrupt();
            _interruptSequence = InterruptSequence.Nmi;

            // The acknowledge stays an OpcodeFetch machine cycle so the generic
            // M1 pin sequencing (/M1 low, I:R refresh, R increment) runs; the
            // extra fifth T-state is the Internal predecrement cycle that
            // follows, mirroring how RST spends its 5-T M1.
            return;
        }

        if (!intInhibited && _intSampledLow && IFF1)
        {
            WakeFromHaltForInterrupt();
            IFF1 = false;
            IFF2 = false;
            _interruptSequence = InterruptSequence.Int;

            // The first cycle becomes an interrupt acknowledge: /M1 + /IORQ low
            // with the two built-in wait states (ApplyPendingCycleTransition
            // wires those from the machine-cycle type).
            _machineCycleType = MachineCycleType.InterruptAck;
        }
    }

    private void WakeFromHaltForInterrupt()
    {
        if (!_halted)
        {
            return;
        }

        _halted = false;
        Halt = true;

        // While halted PC sat on the HALT opcode (every spin M1 skips the
        // increment); step past it so the pushed return address is the
        // instruction after HALT.
        PC.Value++;
    }

    // Runs in place of opcode decode while _interruptSequence is set. Only
    // rising-edge cycle keys are handled; falling-edge keys carry ClkFallingFlag
    // and fall through untouched.
    private void HandleInterruptSequence(int cycleKey)
    {
        if (_interruptSequence == InterruptSequence.Nmi)
        {
            switch (cycleKey)
            {
                case OpcodeFetchT4:
                    // Save the maskable enable into IFF2 and clear IFF1 so the
                    // handler runs with /INT disabled and RETN can restore it.
                    IFF2 = IFF1;
                    IFF1 = false;
                    SetNextCycle(MachineCycleType.Internal);
                    break;

                case InternalT1:
                    SP.Value--;
                    SetNextCycle(MachineCycleType.MemoryWrite);
                    break;

                case MemoryWriteT1:
                    Address = SP.Value;
                    Data = _machineCycle == 3 ? PC.Hi : PC.Lo;
                    break;

                case MemoryWriteT3:
                    if (_machineCycle == 3)
                    {
                        SP.Value--;
                        SetNextCycle(MachineCycleType.MemoryWrite);
                    }
                    else
                    {
                        PC.Value = 0x0066;
                        WZ.Value = 0x0066;
                        _interruptSequence = InterruptSequence.None;
                        SetNextCycle(MachineCycleType.OpcodeFetch);
                    }
                    break;
            }

            return;
        }

        // Maskable /INT acknowledge.
        switch (cycleKey)
        {
            case InterruptAckT3:
                // Whatever the interrupting device jammed on the bus. IM 2 uses
                // it as the low half of the vector-table pointer; IM 0 decodes
                // it as an instruction (only RST is modelled); IM 1 ignores it.
                _tmp = Data;
                break;

            case InterruptAckT4:
                SetNextCycle(MachineCycleType.Internal);
                break;

            case InternalT1:
                SP.Value--;
                SetNextCycle(MachineCycleType.MemoryWrite);
                break;

            case MemoryWriteT1:
                Address = SP.Value;
                Data = _machineCycle == 3 ? PC.Hi : PC.Lo;
                break;

            case MemoryWriteT3:
                if (_machineCycle == 3)
                {
                    SP.Value--;
                    SetNextCycle(MachineCycleType.MemoryWrite);
                }
                else if (IM == 2)
                {
                    SetNextCycle(MachineCycleType.MemoryRead);
                }
                else
                {
                    // IM 1 forces 0x0038; IM 0 with the usual 0xFF on the bus
                    // decodes to RST 38h - the same address. A general jammed
                    // RST opcode (11 ttt 111) targets (byte & 0x38).
                    var target = (ushort)(IM == 0 ? (_tmp & 0x38) : 0x38);
                    PC.Value = target;
                    WZ.Value = target;
                    _interruptSequence = InterruptSequence.None;
                    SetNextCycle(MachineCycleType.OpcodeFetch);
                }
                break;

            case MemoryReadT1:
                // IM 2: the handler address is read from (I:byte) and (I:byte+1).
                Address = _machineCycle == 5
                    ? (ushort)((I << 8) | _tmp)
                    : (ushort)(((I << 8) | _tmp) + 1);
                break;

            case MemoryReadT3:
                if (_machineCycle == 5)
                {
                    WZ.Z = Data;
                    SetNextCycle(MachineCycleType.MemoryRead);
                }
                else
                {
                    WZ.W = Data;
                    PC.Value = WZ.Value;
                    _interruptSequence = InterruptSequence.None;
                    SetNextCycle(MachineCycleType.OpcodeFetch);
                }
                break;
        }
    }
}
