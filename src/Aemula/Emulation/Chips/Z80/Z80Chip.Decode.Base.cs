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
// Implemented here: NOP (0x00); LD r,r' / LD r,(HL) / LD (HL),r (0x40..0x7F bar
// 0x76); HALT (0x76); LD r,n and LD (HL),n (0x06..0x3E); LD A,(BC/DE) and
// LD (BC/DE),A (0x02/0x0A/0x12/0x1A); LD A,(nn) and LD (nn),A (0x3A/0x32);
// LD dd,nn (0x01/0x11/0x21/0x31); LD (nn),HL and LD HL,(nn) (0x22/0x2A);
// LD SP,HL (0xF9); PUSH/POP qq (0xC1..0xF5); EX DE,HL (0xEB); EX AF,AF' (0x08);
// EXX (0xD9); EX (SP),HL (0xE3); JP nn (0xC3). Every other opcode throws until a
// later decode group fills it in.

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

            case 3:
                HandleUnprefixedX3(cycleKey, y, z, p, q);
                return;
        }

        // x == 2 is the 8-bit ALU block, filled in by a later decode group.
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

    private void HandleUnprefixedX3(int cycleKey, int y, int z, int p, int q)
    {
        switch (z)
        {
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
