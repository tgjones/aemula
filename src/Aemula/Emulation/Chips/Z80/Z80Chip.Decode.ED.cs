namespace Aemula.Emulation.Chips.Z80;

// ED-prefixed opcode microcode.
//
// The opcode byte splits (z80.info/decoding.htm) as [x x][y y y][z z z] with
// p = y >> 1, q = y & 1. The useful rows are:
//
//   x == 1 (0x40..0x7F)
//     z == 0   IN r[y],(C)          (y == 6: IN (C), flags only)
//     z == 1   OUT (C),r[y]         (y == 6: OUT (C),0 - NMOS drives 0)
//     z == 2   SBC HL,rp[p] (q==0) / ADC HL,rp[p] (q==1)
//     z == 3   LD (nn),rp[p] (q==0) / LD rp[p],(nn) (q==1)
//     z == 4   NEG                  (every y - 44 documented, 4C/54/.../7C not)
//     z == 5   RETN                 (y == 1: RETI)
//     z == 6   IM im[y]             im[] = 0 0 1 2 0 0 1 2
//     z == 7   y: 0 LD I,A  1 LD R,A  2 LD A,I  3 LD A,R  4 RRD  5 RLD
//   x == 2 (0xA0..0xBF), y >= 4, z <= 3
//     the block instructions: y 4/5/6/7 = increment / decrement / increment
//     repeat / decrement repeat; z 0/1/2/3 = load / compare / IN / OUT.
//
// Everything else in the ED page is an 8-T no-op (two M1s, no side effects) on
// the NMOS part.
//
// Timings are the Zilog Z80 CPU User Manual machine-cycle counts; the
// undocumented block-op and IN/OUT flag formulas are "The Undocumented Z80
// Documented" (Sean Young), chapters 4-5. Only the last iteration of a
// repeating block instruction leaves observable flags - each pass overwrites F
// - so the plain LDI/CPI/INI/OUTI flag rule is applied on every pass. The extra
// undocumented flags an NMOS Z80 leaves when a repeat is aborted mid-flight
// (interrupt, or an opcode that overwrote itself) are handled for the memory
// repeats - LDIR/LDDR take Y/X from PC bits 13/11 on the 5-T repeat tail - but
// deliberately not for the block-I/O repeats: raxoft z80test subtests 102/103
// (INIR->NOP' / INDR->NOP') want the "block-I/O interrupted" formula, and
// applying it regresses the FUSE edb2_1 bus-timing vector, so the two cannot
// both be satisfied on this core.

public sealed partial class Z80Chip
{
    private static readonly byte[] InterruptModeForY = { 0, 0, 1, 2, 0, 0, 1, 2 };

    private void HandleEdPrefixed(int cycleKey)
    {
        var x = _ir >> 6;
        var y = (_ir >> 3) & 0x7;
        var z = _ir & 0x7;
        var p = y >> 1;
        var q = y & 1;

        if (x == 2 && y >= 4 && z <= 3)
        {
            HandleBlockInstruction(cycleKey, y, z);
            return;
        }

        if (x != 1)
        {
            // Undefined ED opcode: behaves as a NOP that still spent a whole
            // second M1 (and its refresh) decoding to nothing.
            if (cycleKey == OpcodeFetchT4)
            {
                FinishPrefixedInstruction();
            }

            return;
        }

        switch (z)
        {
            case 0: HandleInPortC(cycleKey, y); return;
            case 1: HandleOutPortC(cycleKey, y); return;
            case 2: HandleAdcSbcHl(cycleKey, q, p); return;
            case 3: HandleLoad16Absolute(cycleKey, q, p); return;

            case 4: // NEG
                if (cycleKey == OpcodeFetchT4)
                {
                    Neg();
                    FinishPrefixedInstruction();
                }
                return;

            case 5: HandleReturnFromInterrupt(cycleKey, isReti: y == 1); return;

            case 6: // IM im[y]
                if (cycleKey == OpcodeFetchT4)
                {
                    IM = InterruptModeForY[y];
                    FinishPrefixedInstruction();
                }
                return;

            case 7: HandleEdZ7(cycleKey, y); return;
        }
    }

    // IN r[y],(C): a 4-T M1 plus a 4-T I/O read cycle addressed by BC. The byte
    // sets S/Z/P (parity), clears H and N and leaves C; for y != 6 it is also
    // stored in r[y]. WZ = BC + 1.
    private void HandleInPortC(int cycleKey, int y)
    {
        switch (cycleKey)
        {
            case OpcodeFetchT4:
                WZ.Value = (ushort)(BC.Value + 1);
                SetNextCycle(MachineCycleType.IoRead);
                break;

            case IoReadT1:
                Address = BC.Value;
                break;

            case IoReadT3:
                SetInputFlags(Data);
                if (y != 6)
                {
                    Register8(y) = Data;
                }
                FinishPrefixedInstruction();
                break;
        }
    }

    // OUT (C),r[y]: M1 plus a 4-T I/O write cycle addressed by BC. y == 6 is
    // the undocumented OUT (C),0 (the NMOS part puts 0x00 on the bus). No
    // flags. WZ = BC + 1.
    private void HandleOutPortC(int cycleKey, int y)
    {
        switch (cycleKey)
        {
            case OpcodeFetchT4:
                WZ.Value = (ushort)(BC.Value + 1);
                _tmp = y == 6 ? (byte)0 : Register8(y);
                SetNextCycle(MachineCycleType.IoWrite);
                break;

            case IoWriteT1:
                Address = BC.Value;
                Data = _tmp;
                break;

            case IoWriteT3:
                FinishPrefixedInstruction();
                break;
        }
    }

    // ADC HL,ss / SBC HL,ss: the 16-bit arithmetic is done at M1 time; the 7
    // internal T-states that follow (a 4-T then a 3-T internal cycle) are pure
    // padding. Full flags, WZ = HL + 1 latched before the sum.
    private void HandleAdcSbcHl(int cycleKey, int q, int p)
    {
        switch (cycleKey)
        {
            case OpcodeFetchT4:
                if (q == 0)
                {
                    Sbc16FromHl(RegisterPair16(p));
                }
                else
                {
                    Adc16ToHl(RegisterPair16(p));
                }
                SetNextCycle(MachineCycleType.Internal);
                break;

            case InternalT4 when _machineCycle == 2:
                SetNextCycle(MachineCycleType.Internal);
                break;

            case InternalT3 when _machineCycle == 3:
                FinishPrefixedInstruction();
                break;
        }
    }

    // LD (nn),rp[p] / LD rp[p],(nn): read the two address bytes into WZ, then
    // either write rp low/high to WZ / WZ+1 or read them back. WZ ends at
    // nn + 1. rp[] here is BC DE HL SP.
    private void HandleLoad16Absolute(int cycleKey, int q, int p)
    {
        switch (cycleKey)
        {
            case OpcodeFetchT4:
                SetNextCycle(MachineCycleType.MemoryRead);
                break;

            case MemoryReadT1:
                Address = _machineCycle <= 3 ? PC.Value : WZ.Value;
                break;

            case MemoryReadT3:
                switch (_machineCycle)
                {
                    case 2:
                        WZ.Z = Data;
                        PC.Value++;
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case 3:
                        WZ.W = Data;
                        PC.Value++;
                        SetNextCycle(q == 0
                            ? MachineCycleType.MemoryWrite
                            : MachineCycleType.MemoryRead);
                        break;

                    case 4:
                        RegisterPairLow(p) = Data;
                        WZ.Value++;
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case 5:
                        RegisterPairHigh(p) = Data;
                        FinishPrefixedInstruction();
                        break;
                }
                break;

            case MemoryWriteT1:
                Address = WZ.Value;
                if (_machineCycle == 4)
                {
                    Data = RegisterPairLow(p);
                    WZ.Value++;
                }
                else
                {
                    Data = RegisterPairHigh(p);
                }
                break;

            case MemoryWriteT3:
                if (_machineCycle == 4)
                {
                    SetNextCycle(MachineCycleType.MemoryWrite);
                }
                else
                {
                    FinishPrefixedInstruction();
                }
                break;
        }
    }

    // RETN / RETI: pop PC exactly like RET (WZ = the popped address) and copy
    // IFF2 back into IFF1. RETI additionally acknowledges the peripheral
    // daisy-chain, which needs the interrupt logic and is added there.
    private void HandleReturnFromInterrupt(int cycleKey, bool isReti)
    {
        _ = isReti;

        switch (cycleKey)
        {
            case OpcodeFetchT4:
                SetNextCycle(MachineCycleType.MemoryRead);
                break;

            case MemoryReadT1:
                Address = SP.Value;
                break;

            case MemoryReadT3:
                if (_machineCycle == 2)
                {
                    WZ.Z = Data;
                    SP.Value++;
                    SetNextCycle(MachineCycleType.MemoryRead);
                }
                else
                {
                    WZ.W = Data;
                    SP.Value++;
                    PC.Value = WZ.Value;
                    IFF1 = IFF2;
                    FinishPrefixedInstruction();
                }
                break;
        }
    }

    // ED z == 7: the interrupt/refresh-register loads and the nibble rotates.
    private void HandleEdZ7(int cycleKey, int y)
    {
        switch (y)
        {
            case 0: // LD I,A - M1 plus a 1-T internal cycle
            case 1: // LD R,A
                switch (cycleKey)
                {
                    case OpcodeFetchT4:
                        SetNextCycle(MachineCycleType.Internal);
                        break;

                    case InternalT1:
                        if (y == 0)
                        {
                            I = AF.A;
                        }
                        else
                        {
                            R = AF.A;
                        }
                        FinishPrefixedInstruction();
                        break;
                }
                return;

            case 2: // LD A,I
            case 3: // LD A,R
                switch (cycleKey)
                {
                    case OpcodeFetchT4:
                        SetNextCycle(MachineCycleType.Internal);
                        break;

                    case InternalT1:
                        AF.A = y == 2 ? I : R;

                        // "The Undocumented Z80 Documented": if an interrupt is
                        // accepted during this internal T-state, the P/V <- IFF2
                        // copy is lost and P/V comes out clear. This T-state is
                        // the instruction's last, so a latched /NMI edge or a
                        // /INT that IFF1 would honour hits exactly here.
                        var interruptTaken = _nmiPending || (_intSampledLow && IFF1);
                        SetLdAInterruptRegisterFlags(AF.A, interruptTaken);
                        FinishPrefixedInstruction();
                        break;
                }
                return;

            case 4: // RRD
            case 5: // RLD
                switch (cycleKey)
                {
                    case OpcodeFetchT4:
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Address = HL.Value;
                        break;

                    case MemoryReadT3:
                        _tmp = y == 4 ? Rrd(Data) : Rld(Data);
                        SetNextCycle(MachineCycleType.Internal);
                        break;

                    case InternalT4:
                        SetNextCycle(MachineCycleType.MemoryWrite);
                        break;

                    case MemoryWriteT1:
                        Address = HL.Value;
                        Data = _tmp;
                        break;

                    case MemoryWriteT3:
                        FinishPrefixedInstruction();
                        break;
                }
                return;

            default: // y == 6 / 7: undefined, NOP
                if (cycleKey == OpcodeFetchT4)
                {
                    FinishPrefixedInstruction();
                }
                return;
        }
    }

    // The block instructions. y: 4 = up, 5 = down, 6 = up + repeat, 7 = down +
    // repeat. z: 0 = LD, 1 = CP, 2 = IN, 3 = OUT.
    //
    // Machine-cycle map after the two M1s:
    //   LD  : read (HL), write (DE), 2-T internal
    //   CP  : read (HL), 5-T internal (a 4-T then a 1-T internal cycle)
    //   IN  : 1-T internal, I/O read (BC), write (HL)
    //   OUT : 1-T internal, read (HL), I/O write (BC)
    // A repeating form that is not finished adds a 5-T internal tail (a 4-T
    // then a 1-T internal cycle) and re-runs the instruction with PC stepped
    // back onto the ED byte; for LD/CP that tail also sets WZ = PC + 1.
    private void HandleBlockInstruction(int cycleKey, int y, int z)
    {
        var step = (y == 4 || y == 6) ? 1 : -1;
        var repeating = y >= 6;

        switch (z)
        {
            case 0: HandleBlockLoad(cycleKey, step, repeating); return;
            case 1: HandleBlockCompare(cycleKey, step, repeating); return;
            case 2: HandleBlockIn(cycleKey, step, repeating); return;
            case 3: HandleBlockOut(cycleKey, step, repeating); return;
        }
    }

    private void HandleBlockLoad(int cycleKey, int step, bool repeating)
    {
        switch (cycleKey)
        {
            case OpcodeFetchT4:
                SetNextCycle(MachineCycleType.MemoryRead);
                break;

            case MemoryReadT1:
                Address = HL.Value;
                break;

            case MemoryReadT3:
                _tmp = Data;
                SetNextCycle(MachineCycleType.MemoryWrite);
                break;

            case MemoryWriteT1:
                Address = DE.Value;
                Data = _tmp;
                break;

            case MemoryWriteT3:
                HL.Value = (ushort)(HL.Value + step);
                DE.Value = (ushort)(DE.Value + step);
                BC.Value--;
                SetBlockLoadFlags(_tmp, BC.Value != 0);
                SetNextCycle(MachineCycleType.Internal);
                break;

            case InternalT2 when _machineCycle == 4:
                if (repeating && BC.Value != 0)
                {
                    SetNextCycle(MachineCycleType.Internal);
                }
                else
                {
                    FinishPrefixedInstruction();
                }
                break;

            case InternalT4 when _machineCycle == 5:
                SetNextCycle(MachineCycleType.Internal);
                break;

            case InternalT1 when _machineCycle == 6:
                WZ.Value = (ushort)(PC.Value - 1);
                PC.Value -= 2;
                // The 5-T repeat cycle overwrites the undocumented Y/X flags an
                // LDIR/LDDR pass left: they are reloaded from bits 13 and 11 of
                // PC, which now points back at the 0xED prefix. Observable on
                // real NMOS silicon whenever the instruction repeats (an
                // interrupt taken between passes, or - as raxoft's z80test
                // arranges - the copied byte overwriting the opcode so the
                // repeat fetches a NOP).
                Flags.Y = (PC.Value & 0x2000) != 0;
                Flags.X = (PC.Value & 0x0800) != 0;
                FinishPrefixedInstruction();
                break;
        }
    }

    private void HandleBlockCompare(int cycleKey, int step, bool repeating)
    {
        switch (cycleKey)
        {
            case OpcodeFetchT4:
                SetNextCycle(MachineCycleType.MemoryRead);
                break;

            case MemoryReadT1:
                Address = HL.Value;
                break;

            case MemoryReadT3:
                _tmp = Data;
                HL.Value = (ushort)(HL.Value + step);
                BC.Value--;
                WZ.Value = (ushort)(WZ.Value + step);
                SetBlockCompareFlags(_tmp, BC.Value != 0);
                SetNextCycle(MachineCycleType.Internal);
                break;

            case InternalT4 when _machineCycle == 3:
                SetNextCycle(MachineCycleType.Internal);
                break;

            case InternalT1 when _machineCycle == 4:
                if (repeating && BC.Value != 0 && AF.A != _tmp)
                {
                    SetNextCycle(MachineCycleType.Internal);
                }
                else
                {
                    FinishPrefixedInstruction();
                }
                break;

            case InternalT4 when _machineCycle == 5:
                SetNextCycle(MachineCycleType.Internal);
                break;

            case InternalT1 when _machineCycle == 6:
                WZ.Value = (ushort)(PC.Value - 1);
                PC.Value -= 2;
                FinishPrefixedInstruction();
                break;
        }
    }

    private void HandleBlockIn(int cycleKey, int step, bool repeating)
    {
        switch (cycleKey)
        {
            case OpcodeFetchT4:
                SetNextCycle(MachineCycleType.Internal);
                break;

            case InternalT1 when _machineCycle == 2:
                SetNextCycle(MachineCycleType.IoRead);
                break;

            case IoReadT1:
                Address = BC.Value;
                break;

            case IoReadT3:
                _tmp = Data;
                SetNextCycle(MachineCycleType.MemoryWrite);
                break;

            case MemoryWriteT1:
                Address = HL.Value;
                Data = _tmp;
                break;

            case MemoryWriteT3:
                WZ.Value = (ushort)(BC.Value + step);
                var kAddend = BC.C + step;
                BC.B--;
                HL.Value = (ushort)(HL.Value + step);
                SetBlockIoFlags(_tmp, BC.B, kAddend);
                if (repeating && BC.B != 0)
                {
                    SetNextCycle(MachineCycleType.Internal);
                }
                else
                {
                    FinishPrefixedInstruction();
                }
                break;

            case InternalT4 when _machineCycle == 5:
                SetNextCycle(MachineCycleType.Internal);
                break;

            case InternalT1 when _machineCycle == 6:
                PC.Value -= 2;
                FinishPrefixedInstruction();
                break;
        }
    }

    private void HandleBlockOut(int cycleKey, int step, bool repeating)
    {
        switch (cycleKey)
        {
            case OpcodeFetchT4:
                SetNextCycle(MachineCycleType.Internal);
                break;

            case InternalT1 when _machineCycle == 2:
                SetNextCycle(MachineCycleType.MemoryRead);
                break;

            case MemoryReadT1:
                Address = HL.Value;
                break;

            case MemoryReadT3:
                _tmp = Data;
                BC.B--;
                HL.Value = (ushort)(HL.Value + step);
                WZ.Value = (ushort)(BC.Value + step);
                SetBlockIoFlags(_tmp, BC.B, HL.L);
                SetNextCycle(MachineCycleType.IoWrite);
                break;

            case IoWriteT1:
                Address = BC.Value;
                Data = _tmp;
                break;

            case IoWriteT3:
                if (repeating && BC.B != 0)
                {
                    SetNextCycle(MachineCycleType.Internal);
                }
                else
                {
                    FinishPrefixedInstruction();
                }
                break;

            case InternalT4 when _machineCycle == 5:
                SetNextCycle(MachineCycleType.Internal);
                break;

            case InternalT1 when _machineCycle == 6:
                PC.Value -= 2;
                FinishPrefixedInstruction();
                break;
        }
    }
}
