namespace Aemula.Emulation.Chips.Z80;

// DD / FD (index-register) opcode microcode, plus the DD CB / FD CB double
// prefix.
//
// A DD or FD escape byte re-aims every operand that names HL / H / L at the
// index register IX (for DD) or IY (for FD):
//
//   * an "HL" 16-bit operand becomes IX / IY - ADD IX,rp (rp's HL slot is also
//     IX); LD IX,nn; LD IX,(nn) / LD (nn),IX; INC/DEC IX; PUSH/POP IX;
//     EX (SP),IX; JP (IX); LD SP,IX.
//   * an "H" or "L" 8-bit register becomes IXH / IXL (IYH / IYL) - the
//     undocumented half-register ops: LD IXH,n, LD r,IXL, INC IXH, ADD A,IXL,
//     LD IXH,IXL and so on.
//   * an "(HL)" operand becomes "(IX+d)" / "(IY+d)": a signed displacement byte
//     d is fetched from PC after the opcode, then a 5-T internal address
//     calculation forms the effective address IX + d, and WZ is left holding
//     it. The classic substitution rule (z80.info decode + "The Undocumented
//     Z80 Documented", Sean Young): a "(IX+d)" operand suppresses the
//     H/L -> IXH/IXL rename on the *other* operand of the same instruction, so
//     LD H,(IX+d) loads the real H and LD IXH,(IX+d) does not exist.
//
// A DD / FD in front of an opcode that names none of HL / H / L / (HL) is an
// inert prefix: it burns its 4-T M1 (refresh and all) and the opcode then runs
// from the unprefixed table. A chained DD / FD just re-latches (each link its
// own 4-T M1, as on silicon); DD ED runs the ED page with the DD ignored;
// DD CB / FD CB is the double prefix below. EX DE,HL is famously *not* re-aimed
// by a DD/FD, so it stays inert too.
//
// DD CB d op / FD CB d op: after the two escape M1s a memory read fetches d,
// another fetches the op byte, a 2-T internal cycle runs, then the CB
// operation acts on (IX+d): read, 1-T internal, write (BIT stops after the
// read - no write). BIT b,(IX+d) takes its undocumented flag bits 5/3 from
// WZ.high (= (IX+d) >> 8). For an op byte whose z field names a register
// (z != 6), the undocumented "LD r,rot/res/set (IX+d)" behaviour also copies
// the written-back result into r[z] (there is no copy for BIT).
//
// Timings are the Zilog Z80 CPU User Manual machine-cycle counts: every form
// here is exactly its unprefixed equivalent plus the 4-T escape M1, plus - for
// an (IX+d) operand - a 3-T displacement fetch and a 5-T internal calculation
// spliced in ahead of the memory access. The documented quirk for
// LD (IX+d),n: n is fetched immediately after d and the calculation is only
// 2 internal T-states, not 5.

public sealed partial class Z80Chip
{
    // The active index register is IY while an FD escape is in force, IX
    // otherwise.
    private bool IndexIsIy => _prefix is Z80Prefix.FD or Z80Prefix.FDCB;

    private ref ushort IndexValue()
    {
        if (IndexIsIy)
        {
            return ref IY.Value;
        }

        return ref IX.Value;
    }

    private ref byte IndexHigh()
    {
        if (IndexIsIy)
        {
            return ref IY.IYH;
        }

        return ref IX.IXH;
    }

    private ref byte IndexLow()
    {
        if (IndexIsIy)
        {
            return ref IY.IYL;
        }

        return ref IX.IXL;
    }

    // r[] for a DD/FD-prefixed opcode with two register operands: as Register8,
    // but the H and L slots name the halves of the active index register.
    // Slot 6 has no register (it is the (IX+d) memory operand).
    private ref byte IndexRegister8(int index)
    {
        switch (index)
        {
            case 4: return ref IndexHigh();
            case 5: return ref IndexLow();
            default: return ref Register8(index);
        }
    }

    // rp[] (BC DE HL SP) for ADD IX,rp: the "HL" slot is the index register.
    private ref ushort IndexRegisterPair16(int p)
    {
        if (p == 2)
        {
            return ref IndexValue();
        }

        return ref RegisterPair16(p);
    }

    private void HandleIndexPrefixed(int cycleKey)
    {
        if (_prefix is Z80Prefix.DDCB or Z80Prefix.FDCB)
        {
            HandleIndexCbPrefixed(cycleKey);
            return;
        }

        if (cycleKey == OpcodeFetchT3)
        {
            // _ir has just been latched with the real opcode. If it names none
            // of HL / H / L / (HL) - or is a chained DD/FD, or a DD ED - the
            // index escape has nothing left to do beyond the 4-T M1 already
            // spent: drop the prefix and let the unprefixed table finish it
            // (that table re-escapes a further DD / FD / ED on its own). A CB
            // is the DD CB double prefix and is kept.
            if (_ir != 0xCB && !IsIndexSpecific(_ir))
            {
                _prefix = Z80Prefix.None;
            }

            return;
        }

        if (_ir == 0xCB)
        {
            // Open the DD CB / FD CB double prefix: the escape M1 is done, a
            // 3-T memory read now fetches the signed displacement d.
            if (cycleKey == OpcodeFetchT4)
            {
                _prefix = _prefix == Z80Prefix.DD ? Z80Prefix.DDCB : Z80Prefix.FDCB;
                SetNextCycle(MachineCycleType.MemoryRead);
            }

            return;
        }

        // On T1 / T2 of the opcode M1 _ir still holds the escape byte; its octal
        // split lands in x == 3 with no matching case, so the dispatch below is
        // a harmless no-op until _ir is latched on T3.
        var x = _ir >> 6;
        var y = (_ir >> 3) & 0x7;
        var z = _ir & 0x7;
        var p = y >> 1;
        var q = y & 1;

        switch (x)
        {
            case 0: HandleIndexX0(cycleKey, y, z, p, q); return;
            case 1: HandleIndexX1(cycleKey, y, z); return;
            case 2: HandleIndexX2(cycleKey, y, z); return;
            case 3: HandleIndexX3(cycleKey); return;
        }
    }

    // Whether a DD/FD escape actually changes how this opcode behaves - i.e. it
    // names HL, H, L or (HL) somewhere the index register substitutes. Octal
    // split [x x][y y y][z z z], p = y >> 1, q = y & 1 (z80.info decode).
    private static bool IsIndexSpecific(byte op)
    {
        var x = op >> 6;
        var y = (op >> 3) & 0x7;
        var z = op & 0x7;

        switch (x)
        {
            case 0:
                return op switch
                {
                    0x09 or 0x19 or 0x29 or 0x39 => true, // ADD IX,rp
                    0x21 or 0x22 or 0x2A => true,          // LD IX,nn / LD (nn),IX / LD IX,(nn)
                    0x23 or 0x2B => true,                  // INC IX / DEC IX
                    // INC/DEC r[y] (z 4/5) and LD r[y],n (z 6) hit an index
                    // half-register or (IX+d) only when y is 4, 5 or 6.
                    _ => z is 4 or 5 or 6 && y is 4 or 5 or 6,
                };

            case 1:
                // LD r[y],r[z]; 0x76 is HALT (inert). Any H / L / (HL) operand
                // makes it index-specific.
                return op != 0x76 && (y is 4 or 5 or 6 || z is 4 or 5 or 6);

            case 2:
                // alu[y] A,r[z]: index-specific when the source is IXH/IXL or
                // (IX+d).
                return z is 4 or 5 or 6;

            default: // x == 3
                // POP IX, EX (SP),IX, PUSH IX, JP (IX), LD SP,IX. Every other
                // x == 3 opcode (EX DE,HL included) is inert under DD/FD.
                return op is 0xE1 or 0xE3 or 0xE5 or 0xE9 or 0xF9;
        }
    }

    // x == 0: ADD IX,rp; LD IX,nn; LD (nn),IX / LD IX,(nn); INC/DEC IX;
    // INC/DEC IXH/IXL/(IX+d); LD IXH/IXL/(IX+d),n.
    private void HandleIndexX0(int cycleKey, int y, int z, int p, int q)
    {
        switch (z)
        {
            case 1 when q == 1: // ADD IX,rp[p] - mirrors ADD HL,rp (4-T then 3-T internal)
                switch (cycleKey)
                {
                    case OpcodeFetchT4:
                        Add16(ref IndexValue(), IndexRegisterPair16(p));
                        SetNextCycle(MachineCycleType.Internal);
                        break;

                    case InternalT4:
                        if (_machineCycle == 2)
                        {
                            SetNextCycle(MachineCycleType.Internal);
                        }

                        break;

                    case InternalT3:
                        if (_machineCycle == 3)
                        {
                            FinishPrefixedInstruction();
                        }

                        break;
                }

                return;

            case 1 when q == 0: // LD IX,nn
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
                            IndexLow() = Data;
                            PC.Value++;
                            SetNextCycle(MachineCycleType.MemoryRead);
                        }
                        else
                        {
                            IndexHigh() = Data;
                            PC.Value++;
                            FinishPrefixedInstruction();
                        }

                        break;
                }

                return;

            case 2: // LD (nn),IX (q == 0) / LD IX,(nn) (q == 1)
                HandleIndexLoad16Absolute(cycleKey, q);
                return;

            case 3: // INC IX (q == 0) / DEC IX (q == 1) - M1 plus a 2-T internal cycle
                switch (cycleKey)
                {
                    case OpcodeFetchT4:
                        IndexValue() = (ushort)(IndexValue() + (q == 0 ? 1 : -1));
                        SetNextCycle(MachineCycleType.Internal);
                        break;

                    case InternalT2:
                        FinishPrefixedInstruction();
                        break;
                }

                return;

            case 4: // INC r[y]
            case 5: // DEC r[y]
                HandleIndexIncDec8(cycleKey, y, isDecrement: z == 5);
                return;

            case 6: // LD r[y],n
                HandleIndexLoadRegN(cycleKey, y);
                return;
        }
    }

    // LD (nn),IX / LD IX,(nn): read the two address bytes into WZ, then write
    // IXL/IXH to WZ / WZ+1 or read them back. WZ ends at nn + 1. Mirrors the
    // ED-prefixed LD (nn),rp / LD rp,(nn).
    private void HandleIndexLoad16Absolute(int cycleKey, int q)
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
                        IndexLow() = Data;
                        WZ.Value++;
                        SetNextCycle(MachineCycleType.MemoryRead);
                        break;

                    case 5:
                        IndexHigh() = Data;
                        FinishPrefixedInstruction();
                        break;
                }

                break;

            case MemoryWriteT1:
                Address = WZ.Value;
                if (_machineCycle == 4)
                {
                    Data = IndexLow();
                    WZ.Value++;
                }
                else
                {
                    Data = IndexHigh();
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

    // INC/DEC r[y] with y in {4, 5, 6}: the half-register forms are a pure 4-T
    // M1; (IX+d) reads the byte, spends one internal T-state on the +/-1, then
    // writes it back.
    private void HandleIndexIncDec8(int cycleKey, int y, bool isDecrement)
    {
        if (y != 6)
        {
            if (cycleKey == OpcodeFetchT4)
            {
                ref var r = ref IndexRegister8(y);
                r = isDecrement ? Alu8Dec(r) : Alu8Inc(r);
                FinishPrefixedInstruction();
            }

            return;
        }

        if (RunIndexDisplacementPreamble(cycleKey, MachineCycleType.MemoryRead))
        {
            return;
        }

        switch (cycleKey)
        {
            case MemoryReadT1 when _machineCycle == 5:
                Address = WZ.Value;
                break;

            case MemoryReadT3 when _machineCycle == 5:
                _tmp = isDecrement ? Alu8Dec(Data) : Alu8Inc(Data);
                SetNextCycle(MachineCycleType.Internal);
                break;

            case InternalT1 when _machineCycle == 6:
                SetNextCycle(MachineCycleType.MemoryWrite);
                break;

            case MemoryWriteT1 when _machineCycle == 7:
                Address = WZ.Value;
                Data = _tmp;
                break;

            case MemoryWriteT3 when _machineCycle == 7:
                FinishPrefixedInstruction();
                break;
        }
    }

    // LD r[y],n with y in {4, 5, 6}: LD IXH,n / LD IXL,n are M1 plus a 3-T
    // immediate read; LD (IX+d),n is the quirk form - n is fetched immediately
    // after d and the address calculation is only 2 internal T-states.
    private void HandleIndexLoadRegN(int cycleKey, int y)
    {
        if (y != 6)
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
                    IndexRegister8(y) = Data;
                    PC.Value++;
                    FinishPrefixedInstruction();
                    break;
            }

            return;
        }

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
                    _displacement = (sbyte)Data;
                    PC.Value++;
                    SetNextCycle(MachineCycleType.MemoryRead);
                }
                else
                {
                    _tmp = Data;
                    PC.Value++;
                    WZ.Value = (ushort)(IndexValue() + _displacement);
                    SetNextCycle(MachineCycleType.Internal);
                }

                break;

            case InternalT2 when _machineCycle == 4:
                SetNextCycle(MachineCycleType.MemoryWrite);
                break;

            case MemoryWriteT1 when _machineCycle == 5:
                Address = WZ.Value;
                Data = _tmp;
                break;

            case MemoryWriteT3 when _machineCycle == 5:
                FinishPrefixedInstruction();
                break;
        }
    }

    // x == 1: LD r[y],r[z]. (0x76 is HALT and never reaches here.)
    private void HandleIndexX1(int cycleKey, int y, int z)
    {
        if (z == 6)
        {
            // LD r[y],(IX+d) - the (IX+d) operand suppresses the H/L rename, so
            // r[y] is the plain register.
            HandleIndexReadToRegister(cycleKey, isAlu: false, operand: y);
            return;
        }

        if (y == 6)
        {
            // LD (IX+d),r[z] - r[z] is the plain register for the same reason.
            if (RunIndexDisplacementPreamble(cycleKey, MachineCycleType.MemoryWrite))
            {
                return;
            }

            switch (cycleKey)
            {
                case MemoryWriteT1 when _machineCycle == 5:
                    Address = WZ.Value;
                    Data = Register8(z);
                    break;

                case MemoryWriteT3 when _machineCycle == 5:
                    FinishPrefixedInstruction();
                    break;
            }

            return;
        }

        // LD r[y],r[z] with both operands registers: H / L become IXH / IXL.
        if (cycleKey == OpcodeFetchT4)
        {
            IndexRegister8(y) = IndexRegister8(z);
            FinishPrefixedInstruction();
        }
    }

    // x == 2: alu[y] A,r[z]. z in {4, 5} is IXH/IXL (a pure 4-T M1); z == 6 is
    // (IX+d).
    private void HandleIndexX2(int cycleKey, int y, int z)
    {
        if (z == 6)
        {
            HandleIndexReadToRegister(cycleKey, isAlu: true, operand: y);
            return;
        }

        if (cycleKey == OpcodeFetchT4)
        {
            AluOperation(y, IndexRegister8(z));
            FinishPrefixedInstruction();
        }
    }

    // The read side shared by LD r,(IX+d) and alu[y] A,(IX+d): d-fetch + 5-T
    // calc + 3-T read, then either store the byte in r[operand] or run ALU
    // operation operand on it.
    private void HandleIndexReadToRegister(int cycleKey, bool isAlu, int operand)
    {
        if (RunIndexDisplacementPreamble(cycleKey, MachineCycleType.MemoryRead))
        {
            return;
        }

        switch (cycleKey)
        {
            case MemoryReadT1 when _machineCycle == 5:
                Address = WZ.Value;
                break;

            case MemoryReadT3 when _machineCycle == 5:
                if (isAlu)
                {
                    AluOperation(operand, Data);
                }
                else
                {
                    Register8(operand) = Data;
                }

                FinishPrefixedInstruction();
                break;
        }
    }

    // x == 3: the index-register PUSH/POP/EX/JP/LD SP forms.
    private void HandleIndexX3(int cycleKey)
    {
        switch (_ir)
        {
            case 0xE1: // POP IX
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
                            IndexLow() = Data;
                            SP.Value++;
                            SetNextCycle(MachineCycleType.MemoryRead);
                        }
                        else
                        {
                            IndexHigh() = Data;
                            SP.Value++;
                            FinishPrefixedInstruction();
                        }

                        break;
                }

                return;

            case 0xE5: // PUSH IX - M1, a 1-T internal cycle, then the two stack writes
                switch (cycleKey)
                {
                    case OpcodeFetchT4:
                        SetNextCycle(MachineCycleType.Internal);
                        break;

                    case InternalT1:
                        SP.Value--;
                        SetNextCycle(MachineCycleType.MemoryWrite);
                        break;

                    case MemoryWriteT1:
                        Address = SP.Value;
                        Data = _machineCycle == 3 ? IndexHigh() : IndexLow();
                        break;

                    case MemoryWriteT3:
                        if (_machineCycle == 3)
                        {
                            SP.Value--;
                            SetNextCycle(MachineCycleType.MemoryWrite);
                        }
                        else
                        {
                            FinishPrefixedInstruction();
                        }

                        break;
                }

                return;

            case 0xE9: // JP (IX) - PC = IX, no WZ change
                if (cycleKey == OpcodeFetchT4)
                {
                    PC.Value = IndexValue();
                    FinishPrefixedInstruction();
                }

                return;

            case 0xF9: // LD SP,IX - M1 plus a 2-T internal cycle
                switch (cycleKey)
                {
                    case OpcodeFetchT4:
                        SP.Value = IndexValue();
                        SetNextCycle(MachineCycleType.Internal);
                        break;

                    case InternalT2:
                        FinishPrefixedInstruction();
                        break;
                }

                return;

            case 0xE3: // EX (SP),IX
                HandleIndexExSp(cycleKey);
                return;
        }
    }

    // EX (SP),IX: read (SP) -> Z, read (SP+1) -> W, a 1-T internal cycle,
    // write IXH -> (SP+1), write IXL -> (SP), a 2-T internal cycle, then
    // IX = WZ. Mirrors EX (SP),HL; MEMPTR ends holding the value swapped into
    // the index register.
    private void HandleIndexExSp(int cycleKey)
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
                    Data = IndexHigh();
                }
                else
                {
                    Address = SP.Value;
                    Data = IndexLow();
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
                    IndexValue() = WZ.Value;
                    FinishPrefixedInstruction();
                }

                break;
        }
    }

    // The common (IX+d) operand preamble for INC/DEC (IX+d), LD r,(IX+d),
    // LD (IX+d),r and alu A,(IX+d): machine cycle 2 fetches the signed
    // displacement d from PC, then a 5-T internal address calculation (mc 3 is
    // 4 T-states, mc 4 is 1) forms WZ = index + d and stages <access> for the
    // operand cycle at mc 5. Returns true while the preamble owns the tick so
    // the caller returns immediately.
    private bool RunIndexDisplacementPreamble(int cycleKey, MachineCycleType access)
    {
        switch (cycleKey)
        {
            case OpcodeFetchT4:
                SetNextCycle(MachineCycleType.MemoryRead);
                return true;

            case MemoryReadT1 when _machineCycle == 2:
                Address = PC.Value;
                return true;

            case MemoryReadT3 when _machineCycle == 2:
                _displacement = (sbyte)Data;
                PC.Value++;
                SetNextCycle(MachineCycleType.Internal);
                return true;

            case InternalT4 when _machineCycle == 3:
                WZ.Value = (ushort)(IndexValue() + _displacement);
                SetNextCycle(MachineCycleType.Internal);
                return true;

            case InternalT1 when _machineCycle == 4:
                SetNextCycle(access);
                return true;

            default:
                return false;
        }
    }

    // DD CB d op / FD CB d op. Machine cycles after the two escape M1s:
    //   mc 2  3-T read  - the signed displacement d
    //   mc 3  3-T read  - the CB operation byte (kept in _ir)
    //   mc 4  2-T internal (the d + op fetches already covered three of the
    //         five address-calculation T-states the single-prefix form spends)
    //   mc 5  3-T read  - the operand at (IX+d)
    //   mc 6  1-T internal
    //   mc 7  3-T write - the result back to (IX+d)  (skipped for BIT)
    private void HandleIndexCbPrefixed(int cycleKey)
    {
        switch (cycleKey)
        {
            case MemoryReadT1 when _machineCycle == 2:
            case MemoryReadT1 when _machineCycle == 3:
                Address = PC.Value;
                break;

            case MemoryReadT3 when _machineCycle == 2:
                _displacement = (sbyte)Data;
                PC.Value++;
                SetNextCycle(MachineCycleType.MemoryRead);
                break;

            case MemoryReadT3 when _machineCycle == 3:
                _ir = Data;
                PC.Value++;
                // The indexed CB forms do update MEMPTR: WZ = (IX+d).
                WZ.Value = (ushort)(IndexValue() + _displacement);
                SetNextCycle(MachineCycleType.Internal);
                break;

            case InternalT2 when _machineCycle == 4:
                SetNextCycle(MachineCycleType.MemoryRead);
                break;

            case MemoryReadT1 when _machineCycle == 5:
                Address = WZ.Value;
                break;

            case MemoryReadT3 when _machineCycle == 5:
                ApplyIndexCbOperation(Data);
                SetNextCycle(MachineCycleType.Internal);
                break;

            case InternalT1 when _machineCycle == 6:
                if ((_ir >> 6) == 1) // BIT - no write-back
                {
                    FinishPrefixedInstruction();
                }
                else
                {
                    SetNextCycle(MachineCycleType.MemoryWrite);
                }

                break;

            case MemoryWriteT1 when _machineCycle == 7:
                Address = WZ.Value;
                Data = _tmp;
                break;

            case MemoryWriteT3 when _machineCycle == 7:
                FinishPrefixedInstruction();
                break;
        }
    }

    // Run the CB operation named by _ir on the byte read from (IX+d). For every
    // form except BIT the result (in _tmp) is written back to (IX+d) and, when
    // the op byte's z field names a register (z != 6), also copied into r[z] -
    // the undocumented "LD r,rot/res/set (IX+d)". BIT b,(IX+d) writes nothing
    // and takes its undocumented flag bits 5/3 from WZ.high (= (IX+d) >> 8).
    private void ApplyIndexCbOperation(byte value)
    {
        var x = _ir >> 6;
        var y = (_ir >> 3) & 0x7;
        var z = _ir & 0x7;

        switch (x)
        {
            case 0: _tmp = AluRotateShift(y, value); break;
            case 1: AluBit(y, value, WZ.W); return;
            case 2: _tmp = (byte)(value & ~(1 << y)); break;
            case 3: _tmp = (byte)(value | (1 << y)); break;
        }

        if (z != 6)
        {
            Register8(z) = _tmp;
        }
    }
}
