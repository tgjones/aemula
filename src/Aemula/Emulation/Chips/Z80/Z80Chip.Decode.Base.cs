using System;

namespace Aemula.Emulation.Chips.Z80;

// Unprefixed (no 0xCB / 0xED / 0xDD / 0xFD escape) opcode microcode.
//
// The decode is structured around the octal split of the opcode byte from
// z80.info/decoding.htm. Laying the byte out as [x x][y y y][z z z] with
// p = y >> 1 and q = y & 1, whole instruction families collapse to one arm:
// every LD r[y],r[z] is a single case, every LD rp[p],nn another, and so on.
// r[]   = B C D E H L (HL) A          (index 6 = the (HL) memory operand)
// rp[]  = BC DE HL SP                 (the pair table that includes SP)
// rp2[] = BC DE HL AF                 (the pair table that includes AF)
//
// Bus timing is taken from the Zilog Z80 CPU User Manual (UM0080) machine-cycle
// diagrams: an opcode fetch (M1) is 4 T-states, a memory read or write is 3,
// and an internal (no-bus) padding cycle is 1 or 2. Each case label names the
// T-state on whose CLK rising edge the work happens; the generic M1, refresh
// and /MREQ//RD//WR pin sequencing lives in Z80Chip.cs.
//
// MEMPTR (the internal WZ pair) is maintained for the ops that expose it
// through undocumented flag behaviour, following "The Undocumented Z80
// Documented" (Sean Young):
//   LD A,(BC) / LD A,(DE)     WZ = rp + 1
//   LD (BC),A / LD (DE),A     WZ.low = (rp + 1) & 0xFF, WZ.high = A
//   LD A,(nn)                 WZ = nn + 1
//   LD (nn),A                 WZ.low = (nn + 1) & 0xFF, WZ.high = A
//   LD HL,(nn) / LD (nn),HL   WZ = nn + 1
//   EX (SP),HL                WZ = the value loaded into HL
//   JP nn                     WZ = nn
//
// Implemented here: the 8-bit loads (NOP, LD r,r' / LD r,(HL) / LD (HL),r,
// LD r,n, LD (HL),n, LD A,(BC/DE), LD (BC/DE),A, LD A,(nn), LD (nn),A); the
// 16-bit loads and stack ops (LD dd,nn, LD (nn),HL, LD HL,(nn), LD SP,HL,
// PUSH/POP qq, EX DE,HL, EX AF,AF', EXX, EX (SP),HL); the 8-bit ALU
// (ADD/ADC/SUB/SBC/AND/XOR/OR/CP A,r|n|(HL), INC/DEC r|(HL), DAA, CPL, SCF,
// CCF); the 16-bit ALU (ADD HL,ss, INC/DEC ss); the accumulator rotates
// (RLCA/RRCA/RLA/RRA); the jumps and calls (JP nn, JP cc,nn, JP (HL), JR e,
// JR cc,e, DJNZ e, CALL nn, CALL cc,nn, RET, RET cc, RST p); and base-page I/O
// (IN A,(n), OUT (n),A), plus DI/EI. The prefixed tables (CB rotate/shift and
// BIT/RES/SET, ED block ops and 16-bit arithmetic, DD/FD IX/IY) and the
// interrupt sequences still throw.

public sealed partial class Z80Chip
{
    private void HandleInstruction(int cycleKey)
    {
        if (_prefix != Z80Prefix.None)
        {
            throw new NotImplementedException(
                $"Z80 prefixed opcode (prefix {_prefix}, 0x{_ir:X2}) is not implemented yet.");
        }

        var x = _ir >> 6;
        var y = (_ir >> 3) & 0x7;
        var z = _ir & 0x7;
        var p = y >> 1;
        var q = y & 1;

        switch (x)
        {
            case 0:
                HandleUnprefixedX0(cycleKey, y, z, p, q);
                return;

            case 1:
                HandleUnprefixedX1(cycleKey, y, z);
                return;

            case 2:
                HandleUnprefixedX2(cycleKey, y, z);
                return;

            case 3:
                HandleUnprefixedX3(cycleKey, y, z, p, q);
                return;
        }

        throw new NotImplementedException($"Z80 opcode 0x{_ir:X2} is not implemented yet.");
    }

    private void HandleUnprefixedX0(int cycleKey, int y, int z, int p, int q)
    {
        switch (z)
        {
            case 0 when y == 0: // NOP
                if (cycleKey == OpcodeFetchT4)
                {
                    SetNextCycle(MachineCycleType.OpcodeFetch);
                }
                return;

            case 0 when y == 1: // EX AF,AF'
                if (cycleKey == OpcodeFetchT4)
                {
                    // Flags is the live copy; fold it back into AF.F before the
                    // swap and unpack the swapped-in byte afterwards.
                    AF.F = Flags.AsByte();
                    (AF.Value, AFalt) = (AFalt, AF.Value);
                    Flags.SetFromByte(AF.F);
                    SetNextCycle(MachineCycleType.OpcodeFetch);
                }
                return;

            case 0 when y == 2: // DJNZ e
            case 0 when y == 3: // JR e
            case 0: // JR cc[y-4],e
                HandleRelativeJump(cycleKey, y);
                return;

            case 1 when q == 0: // LD rp[p],nn
                switch (cycleKey)
                {
                    case OpcodeFetchT4:
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Address = PC.Value;
                        break;

                    case MemoryReadT3:
                        if (_machineCycle == 2)
                        {
                            RegisterPairLow(p) = Data;
                            PC.Value++;
                            SetNextCycle(MachineCycleType.MemoryRead);
                        }
                        else
                        {
                            RegisterPairHigh(p) = Data;
                            PC.Value++;
                            SetNextCycle(MachineCycleType.OpcodeFetch);
                        }
                        break;
                }
                return;

            case 1: // ADD HL,rp[p]  (q == 1)
                switch (cycleKey)
                {
                    case OpcodeFetchT4:
                        Add16ToHl(RegisterPair16(p));
                        SetNextCycle(MachineCycleType.Internal);
                        break;

                    // 7 internal T-states: one 4-T Internal cycle then a 3-T one.
                    case InternalT4:
                        if (_machineCycle == 2)
                        {
                            SetNextCycle(MachineCycleType.Internal);
                        }
                        break;

                    case InternalT3:
                        if (_machineCycle == 3)
                        {
                            SetNextCycle(MachineCycleType.OpcodeFetch);
                        }
                        break;
                }
                return;

            case 3: // INC rp[p] (q == 0) / DEC rp[p] (q == 1)
                switch (cycleKey)
                {
                    case OpcodeFetchT4:
                        RegisterPair16(p) = (ushort)(RegisterPair16(p) + (q == 0 ? 1 : -1));
                        SetNextCycle(MachineCycleType.Internal);
                        break;

                    // Two internal T-states, no flag or bus activity.
                    case InternalT2:
                        SetNextCycle(MachineCycleType.OpcodeFetch);
                        break;
                }
                return;

            case 4: // INC r[y]
            case 5: // DEC r[y]
                HandleIncDec8(cycleKey, y, isDecrement: z == 5);
                return;

            case 7: // RLCA/RRCA/RLA/RRA/DAA/CPL/SCF/CCF
                if (cycleKey == OpcodeFetchT4)
                {
                    switch (y)
                    {
                        case 0: Rlca(); break;
                        case 1: Rrca(); break;
                        case 2: Rla(); break;
                        case 3: Rra(); break;
                        case 4: Daa(); break;
                        case 5: Cpl(); break;
                        case 6: Scf(); break;
                        case 7: Ccf(); break;
                    }

                    SetNextCycle(MachineCycleType.OpcodeFetch);
                }
                return;

            case 2: // the z=2 "indirect loading" column
                HandleIndirectLoad(cycleKey, p, q);
                return;

            case 6 when y == 6: // LD (HL),n
                switch (cycleKey)
                {
                    case OpcodeFetchT4:
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Address = PC.Value;
                        break;

                    case MemoryReadT3:
                        _tmp = Data;
                        PC.Value++;
                        SetNextCycle(MachineCycleType.MemoryWrite);
                        break;

                    case MemoryWriteT1:
                        Address = HL.Value;
                        Data = _tmp;
                        break;

                    case MemoryWriteT3:
                        SetNextCycle(MachineCycleType.OpcodeFetch);
                        break;
                }
                return;

            case 6: // LD r[y],n
                switch (cycleKey)
                {
                    case OpcodeFetchT4:
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Address = PC.Value;
                        break;

                    case MemoryReadT3:
                        Register8(y) = Data;
                        PC.Value++;
                        SetNextCycle(MachineCycleType.OpcodeFetch);
                        break;
                }
                return;
        }

        throw new NotImplementedException($"Z80 opcode 0x{_ir:X2} is not implemented yet.");
    }

    // z = 2 for x = 0: LD (BC)/(DE),A, LD A,(BC)/(DE), LD (nn),HL, LD HL,(nn),
    // LD (nn),A, LD A,(nn). q picks direction (0 = store, 1 = load); p picks the
    // pointer (0 = BC, 1 = DE, 2 = absolute address with HL, 3 = absolute
    // address with A).
    private void HandleIndirectLoad(int cycleKey, int p, int q)
    {
        if (p <= 1)
        {
            var pointer = p == 0 ? BC.Value : DE.Value;

            if (q == 0) // LD (BC),A / LD (DE),A
            {
                switch (cycleKey)
                {
                    case OpcodeFetchT4:
                        WZ.Z = (byte)(pointer + 1);
                        WZ.W = AF.A;
                        SetNextCycle(MachineCycleType.MemoryWrite);
                        break;

                    case MemoryWriteT1:
                        Address = pointer;
                        Data = AF.A;
                        break;

                    case MemoryWriteT3:
                        SetNextCycle(MachineCycleType.OpcodeFetch);
                        break;
                }
            }
            else // LD A,(BC) / LD A,(DE)
            {
                switch (cycleKey)
                {
                    case OpcodeFetchT4:
                        WZ.Value = (ushort)(pointer + 1);
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Address = pointer;
                        break;

                    case MemoryReadT3:
                        AF.A = Data;
                        SetNextCycle(MachineCycleType.OpcodeFetch);
                        break;
                }
            }

            return;
        }

        // p == 2: 16-bit transfer through HL (one extra memory cycle).
        // p == 3: 8-bit transfer through A.
        var wide = p == 2;

        switch (cycleKey)
        {
            case OpcodeFetchT4:
                SetNextCycle(MachineCycleType.MemoryRead);
                break;

            case MemoryReadT1:
                // Machine cycles 2 and 3 fetch the address operand from PC; the
                // data transfer cycles that follow address WZ (which now holds
                // nn, then nn+1).
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
                        SetNextCycle(q == 0 ? MachineCycleType.MemoryWrite : MachineCycleType.MemoryRead);
                        break;

                    case 4:
                        if (wide)
                        {
                            HL.L = Data;
                            WZ.Value++;
                            SetNextCycle(MachineCycleType.MemoryRead);
                        }
                        else
                        {
                            AF.A = Data;
                            WZ.Value++;
                            SetNextCycle(MachineCycleType.OpcodeFetch);
                        }
                        break;

                    case 5:
                        HL.H = Data;
                        SetNextCycle(MachineCycleType.OpcodeFetch);
                        break;
                }
                break;

            case MemoryWriteT1:
                Address = WZ.Value;
                if (wide)
                {
                    Data = _machineCycle == 4 ? HL.L : HL.H;
                    if (_machineCycle == 4)
                    {
                        WZ.Value++;
                    }
                }
                else
                {
                    Data = AF.A;

                    // LD (nn),A: MEMPTR low = (nn + 1) & 0xFF with no carry into
                    // the high byte, MEMPTR high = A.
                    WZ.Z = (byte)(WZ.Z + 1);
                    WZ.W = AF.A;
                }
                break;

            case MemoryWriteT3:
                SetNextCycle(wide && _machineCycle == 4
                    ? MachineCycleType.MemoryWrite
                    : MachineCycleType.OpcodeFetch);
                break;
        }
    }

    private void HandleUnprefixedX1(int cycleKey, int y, int z)
    {
        if (y == 6 && z == 6) // HALT
        {
            if (cycleKey == OpcodeFetchT4)
            {
                if (!_halted)
                {
                    // Step PC back onto the HALT byte so every following M1
                    // re-fetches it: with PC frozen there (the M1 T1 handler
                    // skips the increment while halted) the CPU spins on 4-T
                    // NOP-equivalent fetches with /HALT asserted until an
                    // interrupt clears the state.
                    _halted = true;
                    Halt = false;
                    PC.Value--;
                }

                SetNextCycle(MachineCycleType.OpcodeFetch);
            }

            return;
        }

        if (z == 6) // LD r[y],(HL)
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
                    Register8(y) = Data;
                    SetNextCycle(MachineCycleType.OpcodeFetch);
                    break;
            }

            return;
        }

        if (y == 6) // LD (HL),r[z]
        {
            switch (cycleKey)
            {
                case OpcodeFetchT4:
                    _tmp = Register8(z);
                    SetNextCycle(MachineCycleType.MemoryWrite);
                    break;

                case MemoryWriteT1:
                    Address = HL.Value;
                    Data = _tmp;
                    break;

                case MemoryWriteT3:
                    SetNextCycle(MachineCycleType.OpcodeFetch);
                    break;
            }

            return;
        }

        // LD r[y],r[z] - a pure 4-T M1.
        if (cycleKey == OpcodeFetchT4)
        {
            Register8(y) = Register8(z);
            SetNextCycle(MachineCycleType.OpcodeFetch);
        }
    }

    // x == 2: alu[y] A,r[z] - the 8-bit ALU with a register (or (HL)) source.
    // y names the operation (0 ADD .. 7 CP); z the source register, z == 6 being
    // the (HL) memory operand that adds a 3-T read.
    private void HandleUnprefixedX2(int cycleKey, int y, int z)
    {
        if (z == 6)
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
                    AluOperation(y, Data);
                    SetNextCycle(MachineCycleType.OpcodeFetch);
                    break;
            }

            return;
        }

        if (cycleKey == OpcodeFetchT4)
        {
            AluOperation(y, Register8(z));
            SetNextCycle(MachineCycleType.OpcodeFetch);
        }
    }

    // JR e (y == 3), JR cc,e (y >= 4, cc = y - 4) and DJNZ e (y == 2).
    //
    // JR cc's condition is a flag test known at M1 time, so it is resolved
    // there: a taken JR / JR cc reads the displacement (a real 3-T operand
    // fetch), then spends 5 internal T-states adding it to PC (WZ = PC), for
    // 12 T; a non-taken JR cc skips the fetch's data phase - its 3 T-states are
    // internal, PC just steps past the byte - for 7 T.
    //
    // DJNZ decrements B in M1's one padding T-state; the fall-through then skips
    // the displacement fetch's data phase just as a non-taken JR cc does, so it
    // is 8 T when B reaches 0 and 13 T when B is still non-zero.
    private void HandleRelativeJump(int cycleKey, int y)
    {
        var isDjnz = y == 2;

        switch (cycleKey)
        {
            case OpcodeFetchT4:
                if (isDjnz)
                {
                    BC.B--;
                    SetNextCycle(MachineCycleType.Internal); // M1's padding T-state
                }
                else if (y == 3 || EvaluateCondition(y - 4))
                {
                    SetNextCycle(MachineCycleType.MemoryRead);
                }
                else
                {
                    // Non-taken JR cc: consume the operand slot without a bus
                    // read (3 internal T-states), then step PC past it.
                    SetNextCycle(MachineCycleType.Internal);
                }
                break;

            case InternalT1 when isDjnz && _machineCycle == 2:
                SetNextCycle(BC.B != 0
                    ? MachineCycleType.MemoryRead
                    : MachineCycleType.Internal);
                break;

            // The non-taken displacement slot: 3 internal T-states, no bus read;
            // machine cycle 2 for JR cc, 3 for DJNZ (which has the extra M1 pad).
            case InternalT3 when !isDjnz && _machineCycle == 2:
            case InternalT3 when isDjnz && _machineCycle == 3:
                PC.Value++;
                SetNextCycle(MachineCycleType.OpcodeFetch);
                break;

            case MemoryReadT1:
                Address = PC.Value;
                break;

            case MemoryReadT3:
                _tmp = Data;
                PC.Value++;
                SetNextCycle(MachineCycleType.Internal);
                break;

            // The internal displacement-add: one 4-T Internal cycle then a 1-T
            // one (5 T-states total).
            case InternalT4 when _machineCycle == (isDjnz ? 4 : 3):
                SetNextCycle(MachineCycleType.Internal);
                break;

            case InternalT1 when _machineCycle == (isDjnz ? 5 : 4):
                PC.Value = (ushort)(PC.Value + (sbyte)_tmp);
                WZ.Value = PC.Value;
                SetNextCycle(MachineCycleType.OpcodeFetch);
                break;
        }
    }

    // INC r[y] / DEC r[y]. A register target is a pure 4-T M1; the (HL) target
    // (y == 6) reads the byte, spends one internal T-state on the +/-1, then
    // writes it back - 11 T-states in all.
    private void HandleIncDec8(int cycleKey, int y, bool isDecrement)
    {
        if (y != 6)
        {
            if (cycleKey == OpcodeFetchT4)
            {
                ref var r = ref Register8(y);
                r = isDecrement ? Alu8Dec(r) : Alu8Inc(r);
                SetNextCycle(MachineCycleType.OpcodeFetch);
            }

            return;
        }

        switch (cycleKey)
        {
            case OpcodeFetchT4:
                SetNextCycle(MachineCycleType.MemoryRead);
                break;

            case MemoryReadT1:
                Address = HL.Value;
                break;

            case MemoryReadT3:
                _tmp = isDecrement ? Alu8Dec(Data) : Alu8Inc(Data);
                SetNextCycle(MachineCycleType.Internal);
                break;

            case InternalT1:
                SetNextCycle(MachineCycleType.MemoryWrite);
                break;

            case MemoryWriteT1:
                Address = HL.Value;
                Data = _tmp;
                break;

            case MemoryWriteT3:
                SetNextCycle(MachineCycleType.OpcodeFetch);
                break;
        }
    }

    // CALL nn and CALL cc,nn. Both operand bytes are always read (WZ = nn); a
    // taken call then spends one internal T-state, pushes the return address
    // high byte, then its low byte, and jumps. Non-taken CALL cc is 10 T; a
    // taken call (conditional or not) is 17 T.
    private void HandleCall(int cycleKey, bool unconditional, int condition)
    {
        switch (cycleKey)
        {
            case OpcodeFetchT4:
                SetNextCycle(MachineCycleType.MemoryRead);
                break;

            case MemoryReadT1:
                Address = PC.Value;
                break;

            case MemoryReadT3:
                if (_machineCycle == 2)
                {
                    WZ.Z = Data;
                    PC.Value++;
                    SetNextCycle(MachineCycleType.MemoryRead);
                }
                else
                {
                    WZ.W = Data;
                    PC.Value++;
                    SetNextCycle(unconditional || EvaluateCondition(condition)
                        ? MachineCycleType.Internal
                        : MachineCycleType.OpcodeFetch);
                }
                break;

            case InternalT1:
                SP.Value--;
                SetNextCycle(MachineCycleType.MemoryWrite);
                break;

            case MemoryWriteT1:
                Address = SP.Value;
                Data = _machineCycle == 5 ? PC.Hi : PC.Lo;
                break;

            case MemoryWriteT3:
                if (_machineCycle == 5)
                {
                    SP.Value--;
                    SetNextCycle(MachineCycleType.MemoryWrite);
                }
                else
                {
                    PC.Value = WZ.Value;
                    SetNextCycle(MachineCycleType.OpcodeFetch);
                }
                break;
        }
    }

    private void HandleUnprefixedX3(int cycleKey, int y, int z, int p, int q)
    {
        switch (z)
        {
            case 0: // RET cc[y]
                switch (cycleKey)
                {
                    // M1 is padded with one internal T-state to evaluate the
                    // condition: a non-taken RET cc is 5 T, a taken one 11 T.
                    case OpcodeFetchT4:
                        SetNextCycle(MachineCycleType.Internal);
                        break;

                    case InternalT1:
                        SetNextCycle(EvaluateCondition(y)
                            ? MachineCycleType.MemoryRead
                            : MachineCycleType.OpcodeFetch);
                        break;

                    case MemoryReadT1:
                        Address = SP.Value;
                        break;

                    case MemoryReadT3:
                        if (_machineCycle == 3)
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
                            SetNextCycle(MachineCycleType.OpcodeFetch);
                        }
                        break;
                }
                return;

            case 1 when q == 1 && p == 0: // RET
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
                            SetNextCycle(MachineCycleType.OpcodeFetch);
                        }
                        break;
                }
                return;

            case 1 when q == 1 && p == 2: // JP (HL) - PC = HL, no WZ change
                if (cycleKey == OpcodeFetchT4)
                {
                    PC.Value = HL.Value;
                    SetNextCycle(MachineCycleType.OpcodeFetch);
                }
                return;

            case 2: // JP cc[y],nn - both operand bytes always read; WZ = nn
                switch (cycleKey)
                {
                    case OpcodeFetchT4:
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Address = PC.Value;
                        break;

                    case MemoryReadT3:
                        if (_machineCycle == 2)
                        {
                            WZ.Z = Data;
                            PC.Value++;
                            SetNextCycle(MachineCycleType.MemoryRead);
                        }
                        else
                        {
                            WZ.W = Data;
                            PC.Value++;
                            if (EvaluateCondition(y))
                            {
                                PC.Value = WZ.Value;
                            }

                            SetNextCycle(MachineCycleType.OpcodeFetch);
                        }
                        break;
                }
                return;

            case 3 when y == 2: // OUT (n),A
                switch (cycleKey)
                {
                    case OpcodeFetchT4:
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Address = PC.Value;
                        break;

                    case MemoryReadT3:
                        _tmp = Data; // the port's low byte, n
                        PC.Value++;
                        // MEMPTR low = (n + 1) & 0xFF (no carry), MEMPTR high = A.
                        WZ.Z = (byte)(_tmp + 1);
                        WZ.W = AF.A;
                        SetNextCycle(MachineCycleType.IoWrite);
                        break;

                    case IoWriteT1:
                        Address = (ushort)((AF.A << 8) | _tmp);
                        Data = AF.A;
                        break;

                    case IoWriteT3:
                        SetNextCycle(MachineCycleType.OpcodeFetch);
                        break;
                }
                return;

            case 3 when y == 3: // IN A,(n)
                switch (cycleKey)
                {
                    case OpcodeFetchT4:
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Address = PC.Value;
                        break;

                    case MemoryReadT3:
                        _tmp = Data; // n
                        PC.Value++;
                        // MEMPTR = (A << 8 | n) + 1.
                        WZ.Value = (ushort)(((AF.A << 8) | _tmp) + 1);
                        SetNextCycle(MachineCycleType.IoRead);
                        break;

                    case IoReadT1:
                        Address = (ushort)((AF.A << 8) | _tmp);
                        break;

                    case IoReadT3:
                        AF.A = Data; // IN A,(n) leaves the flags alone
                        SetNextCycle(MachineCycleType.OpcodeFetch);
                        break;
                }
                return;

            case 3 when y == 6: // DI
                if (cycleKey == OpcodeFetchT4)
                {
                    IFF1 = false;
                    IFF2 = false;
                    SetNextCycle(MachineCycleType.OpcodeFetch);
                }
                return;

            case 3 when y == 7: // EI
                if (cycleKey == OpcodeFetchT4)
                {
                    // The one-instruction /INT shadow after EI is added with the
                    // interrupt sequences; the flip-flops themselves set here.
                    IFF1 = true;
                    IFF2 = true;
                    SetNextCycle(MachineCycleType.OpcodeFetch);
                }
                return;

            case 4: // CALL cc[y],nn
                HandleCall(cycleKey, unconditional: false, condition: y);
                return;

            case 5 when q == 1 && p == 0: // CALL nn
                HandleCall(cycleKey, unconditional: true, condition: 0);
                return;

            case 6: // alu[y] A,n
                switch (cycleKey)
                {
                    case OpcodeFetchT4:
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Address = PC.Value;
                        break;

                    case MemoryReadT3:
                        AluOperation(y, Data);
                        PC.Value++;
                        SetNextCycle(MachineCycleType.OpcodeFetch);
                        break;
                }
                return;

            case 7: // RST y*8
                switch (cycleKey)
                {
                    // M1 padded with one internal T-state before the push: 11 T.
                    case OpcodeFetchT4:
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
                            PC.Value = (ushort)(y * 8);
                            WZ.Value = PC.Value;
                            SetNextCycle(MachineCycleType.OpcodeFetch);
                        }
                        break;
                }
                return;

            case 1 when q == 0: // POP rp2[p]
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
                            RegisterPair2Low(p) = Data;
                            SP.Value++;
                            SetNextCycle(MachineCycleType.MemoryRead);
                        }
                        else
                        {
                            RegisterPair2High(p) = Data;
                            SP.Value++;
                            if (p == 3)
                            {
                                // POP AF landed a new F byte - unpack it.
                                Flags.SetFromByte(AF.F);
                            }

                            SetNextCycle(MachineCycleType.OpcodeFetch);
                        }
                        break;
                }
                return;

            case 1 when q == 1 && p == 1: // EXX
                if (cycleKey == OpcodeFetchT4)
                {
                    (BC.Value, BCalt) = (BCalt, BC.Value);
                    (DE.Value, DEalt) = (DEalt, DE.Value);
                    (HL.Value, HLalt) = (HLalt, HL.Value);
                    SetNextCycle(MachineCycleType.OpcodeFetch);
                }
                return;

            case 1 when q == 1 && p == 3: // LD SP,HL - M1 plus a 2-T internal cycle
                switch (cycleKey)
                {
                    case OpcodeFetchT4:
                        SP.Value = HL.Value;
                        SetNextCycle(MachineCycleType.Internal);
                        break;

                    case InternalT2:
                        SetNextCycle(MachineCycleType.OpcodeFetch);
                        break;
                }
                return;

            case 3 when y == 0: // JP nn
                switch (cycleKey)
                {
                    case OpcodeFetchT4:
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case MemoryReadT1:
                        Address = PC.Value;
                        break;

                    case MemoryReadT3:
                        if (_machineCycle == 2)
                        {
                            WZ.Z = Data;
                            PC.Value++;
                            SetNextCycle(MachineCycleType.MemoryRead);
                        }
                        else
                        {
                            WZ.W = Data;
                            PC.Value = WZ.Value;
                            SetNextCycle(MachineCycleType.OpcodeFetch);
                        }
                        break;
                }
                return;

            case 3 when y == 5: // EX DE,HL
                if (cycleKey == OpcodeFetchT4)
                {
                    (DE.Value, HL.Value) = (HL.Value, DE.Value);
                    SetNextCycle(MachineCycleType.OpcodeFetch);
                }
                return;

            case 3 when y == 4: // EX (SP),HL
                HandleExSpHl(cycleKey);
                return;

            case 5 when q == 0: // PUSH rp2[p]
                switch (cycleKey)
                {
                    case OpcodeFetchT4:
                        if (p == 3)
                        {
                            // PUSH AF pushes the live flags - fold them in first.
                            AF.F = Flags.AsByte();
                        }

                        SetNextCycle(MachineCycleType.Internal);
                        break;

                    case InternalT1:
                        SP.Value--;
                        SetNextCycle(MachineCycleType.MemoryWrite);
                        break;

                    case MemoryWriteT1:
                        Address = SP.Value;
                        Data = _machineCycle == 3 ? RegisterPair2High(p) : RegisterPair2Low(p);
                        break;

                    case MemoryWriteT3:
                        if (_machineCycle == 3)
                        {
                            SP.Value--;
                            SetNextCycle(MachineCycleType.MemoryWrite);
                        }
                        else
                        {
                            SetNextCycle(MachineCycleType.OpcodeFetch);
                        }
                        break;
                }
                return;
        }

        throw new NotImplementedException($"Z80 opcode 0x{_ir:X2} is not implemented yet.");
    }

    // EX (SP),HL: M1, read (SP) -> Z, read (SP+1) -> W, a 1-T internal cycle,
    // write H -> (SP+1), write L -> (SP), a 2-T internal cycle, then HL = WZ.
    // MEMPTR ends up holding the value swapped into HL.
    private void HandleExSpHl(int cycleKey)
    {
        switch (cycleKey)
        {
            case OpcodeFetchT4:
                SetNextCycle(MachineCycleType.MemoryRead);
                break;

            case MemoryReadT1:
                Address = _machineCycle == 2 ? SP.Value : (ushort)(SP.Value + 1);
                break;

            case MemoryReadT3:
                if (_machineCycle == 2)
                {
                    WZ.Z = Data;
                    SetNextCycle(MachineCycleType.MemoryRead);
                }
                else
                {
                    WZ.W = Data;
                    SetNextCycle(MachineCycleType.Internal);
                }
                break;

            case InternalT1:
                if (_machineCycle == 4)
                {
                    SetNextCycle(MachineCycleType.MemoryWrite);
                }
                break;

            case MemoryWriteT1:
                if (_machineCycle == 5)
                {
                    Address = (ushort)(SP.Value + 1);
                    Data = HL.H;
                }
                else
                {
                    Address = SP.Value;
                    Data = HL.L;
                }
                break;

            case MemoryWriteT3:
                SetNextCycle(_machineCycle == 5
                    ? MachineCycleType.MemoryWrite
                    : MachineCycleType.Internal);
                break;

            case InternalT2:
                if (_machineCycle == 7)
                {
                    HL.Value = WZ.Value;
                    SetNextCycle(MachineCycleType.OpcodeFetch);
                }
                break;
        }
    }

    // --- Register-table helpers ------------------------------------------------

    // r[]: B C D E H L (HL) A. Index 6 is the memory operand and has no register.
    private ref byte Register8(int index)
    {
        switch (index)
        {
            case 0: return ref BC.B;
            case 1: return ref BC.C;
            case 2: return ref DE.D;
            case 3: return ref DE.E;
            case 4: return ref HL.H;
            case 5: return ref HL.L;
            case 7: return ref AF.A;
            default: throw new InvalidOperationException();
        }
    }

    // rp[]: BC DE HL SP, as a single 16-bit lvalue for INC/DEC ss and ADD HL,ss.
    private ref ushort RegisterPair16(int p)
    {
        switch (p)
        {
            case 0: return ref BC.Value;
            case 1: return ref DE.Value;
            case 2: return ref HL.Value;
            case 3: return ref SP.Value;
            default: throw new InvalidOperationException();
        }
    }

    // rp[]: BC DE HL SP.
    private ref byte RegisterPairLow(int p)
    {
        switch (p)
        {
            case 0: return ref BC.C;
            case 1: return ref DE.E;
            case 2: return ref HL.L;
            case 3: return ref SP.Lo;
            default: throw new InvalidOperationException();
        }
    }

    private ref byte RegisterPairHigh(int p)
    {
        switch (p)
        {
            case 0: return ref BC.B;
            case 1: return ref DE.D;
            case 2: return ref HL.H;
            case 3: return ref SP.Hi;
            default: throw new InvalidOperationException();
        }
    }

    // rp2[]: BC DE HL AF.
    private ref byte RegisterPair2Low(int p)
    {
        switch (p)
        {
            case 0: return ref BC.C;
            case 1: return ref DE.E;
            case 2: return ref HL.L;
            case 3: return ref AF.F;
            default: throw new InvalidOperationException();
        }
    }

    private ref byte RegisterPair2High(int p)
    {
        switch (p)
        {
            case 0: return ref BC.B;
            case 1: return ref DE.D;
            case 2: return ref HL.H;
            case 3: return ref AF.A;
            default: throw new InvalidOperationException();
        }
    }
}
