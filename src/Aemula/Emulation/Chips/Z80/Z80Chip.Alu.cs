namespace Aemula.Emulation.Chips.Z80;

// The Z80 ALU primitives and the accumulator rotates, split out of the decode
// microcode so the flag rules live in one place. Every formula follows "The
// Undocumented Z80 Documented" (Sean Young):
//
//   S   (bit 7)  result bit 7
//   Z   (bit 6)  result == 0
//   Y   (bit 5)  result bit 5 - except CP, which copies the operand's bit 5
//   H   (bit 4)  carry / borrow out of bit 3 (bit 11 for the 16-bit add)
//   X   (bit 3)  result bit 3 - except CP, which copies the operand's bit 3
//   P/V (bit 2)  signed overflow for add / sub / INC / DEC; even parity for the
//                logical and rotate group
//   N   (bit 1)  0 after an add, 1 after a subtract
//   C   (bit 0)  carry / borrow out of bit 7 (bit 15 for the 16-bit add)
//
// NEG is deliberately not here: it is the ED-prefixed opcode ED 44 and is
// implemented with the rest of the ED table.

public sealed partial class Z80Chip
{
    // "Q": the F byte the most recently completed instruction produced, or 0 if
    // that instruction left F untouched. Refreshed at each instruction boundary
    // from _flagsModified (see the OpcodeFetch branch of ApplyPendingCycle
    // transition). SCF and CCF derive their undocumented bits 5/3 from it - the
    // NMOS-Zilog rule that makes those two opcodes leak the previous result.
    private bool _flagsModified;
    private byte _q;

    // ADD / ADC: carryIn is 0 for ADD, the current carry for ADC.
    private byte Alu8Add(byte a, byte operand, int carryIn)
    {
        var result = a + operand + carryIn;
        var r = (byte)result;

        Flags.Sign = (r & 0x80) != 0;
        Flags.Zero = r == 0;
        Flags.Y = (r & 0x20) != 0;
        Flags.HalfCarry = ((a & 0x0F) + (operand & 0x0F) + carryIn) > 0x0F;
        Flags.X = (r & 0x08) != 0;
        Flags.ParityOverflow = ((a ^ operand ^ 0x80) & (a ^ r) & 0x80) != 0;
        Flags.Subtract = false;
        Flags.Carry = result > 0xFF;
        _flagsModified = true;
        return r;
    }

    // SUB / SBC / CP. CP passes store = false: it discards the result and, per
    // the undocumented rule, takes bits 5/3 of F from the operand instead of
    // from the (unstored) difference.
    private byte Alu8Sub(byte a, byte operand, int carryIn, bool store = true)
    {
        var result = a - operand - carryIn;
        var r = (byte)result;

        Flags.Sign = (r & 0x80) != 0;
        Flags.Zero = r == 0;
        Flags.HalfCarry = ((a & 0x0F) - (operand & 0x0F) - carryIn) < 0;
        Flags.ParityOverflow = ((a ^ operand) & (a ^ r) & 0x80) != 0;
        Flags.Subtract = true;
        Flags.Carry = result < 0;

        var yx = store ? r : operand;
        Flags.Y = (yx & 0x20) != 0;
        Flags.X = (yx & 0x08) != 0;
        _flagsModified = true;
        return r;
    }

    private void Alu8And(byte operand)
    {
        var r = (byte)(AF.A & operand);
        AF.A = r;
        SetLogicalFlags(r, halfCarry: true);
    }

    private void Alu8Xor(byte operand)
    {
        var r = (byte)(AF.A ^ operand);
        AF.A = r;
        SetLogicalFlags(r, halfCarry: false);
    }

    private void Alu8Or(byte operand)
    {
        var r = (byte)(AF.A | operand);
        AF.A = r;
        SetLogicalFlags(r, halfCarry: false);
    }

    // AND sets H; OR and XOR clear it. All three clear N and C and use parity.
    private void SetLogicalFlags(byte r, bool halfCarry)
    {
        Flags.Sign = (r & 0x80) != 0;
        Flags.Zero = r == 0;
        Flags.Y = (r & 0x20) != 0;
        Flags.HalfCarry = halfCarry;
        Flags.X = (r & 0x08) != 0;
        Flags.ParityOverflow = ParityTable[r];
        Flags.Subtract = false;
        Flags.Carry = false;
        _flagsModified = true;
    }

    // INC r / INC (HL): C is left alone, so this is not just Alu8Add with 1.
    private byte Alu8Inc(byte v)
    {
        var r = (byte)(v + 1);
        Flags.Sign = (r & 0x80) != 0;
        Flags.Zero = r == 0;
        Flags.Y = (r & 0x20) != 0;
        Flags.HalfCarry = (v & 0x0F) == 0x0F;
        Flags.X = (r & 0x08) != 0;
        Flags.ParityOverflow = v == 0x7F;
        Flags.Subtract = false;
        _flagsModified = true;
        return r;
    }

    private byte Alu8Dec(byte v)
    {
        var r = (byte)(v - 1);
        Flags.Sign = (r & 0x80) != 0;
        Flags.Zero = r == 0;
        Flags.Y = (r & 0x20) != 0;
        Flags.HalfCarry = (v & 0x0F) == 0x00;
        Flags.X = (r & 0x08) != 0;
        Flags.ParityOverflow = v == 0x80;
        Flags.Subtract = true;
        _flagsModified = true;
        return r;
    }

    // The one operation named by y in the x == 2 block and the alu[y] A,n column
    // of x == 3: 0 ADD, 1 ADC, 2 SUB, 3 SBC, 4 AND, 5 XOR, 6 OR, 7 CP.
    private void AluOperation(int op, byte operand)
    {
        switch (op)
        {
            case 0: AF.A = Alu8Add(AF.A, operand, 0); break;
            case 1: AF.A = Alu8Add(AF.A, operand, Flags.Carry ? 1 : 0); break;
            case 2: AF.A = Alu8Sub(AF.A, operand, 0); break;
            case 3: AF.A = Alu8Sub(AF.A, operand, Flags.Carry ? 1 : 0); break;
            case 4: Alu8And(operand); break;
            case 5: Alu8Xor(operand); break;
            case 6: Alu8Or(operand); break;
            case 7: Alu8Sub(AF.A, operand, 0, store: false); break;
        }
    }

    // DAA: fold A back into packed BCD using H, C and N left by the previous
    // add / subtract. C, once needed, stays set; H is recomputed from the
    // pre-correction low nibble (and, for the subtract case, from the old H).
    private void Daa()
    {
        var a = AF.A;
        var correction = 0;
        var carry = Flags.Carry;

        if (Flags.HalfCarry || (a & 0x0F) > 0x09)
        {
            correction |= 0x06;
        }

        if (carry || a > 0x99)
        {
            correction |= 0x60;
            carry = true;
        }

        var r = (byte)(Flags.Subtract ? a - correction : a + correction);

        Flags.HalfCarry = Flags.Subtract
            ? Flags.HalfCarry && (a & 0x0F) < 0x06
            : (a & 0x0F) > 0x09;
        Flags.Sign = (r & 0x80) != 0;
        Flags.Zero = r == 0;
        Flags.Y = (r & 0x20) != 0;
        Flags.X = (r & 0x08) != 0;
        Flags.ParityOverflow = ParityTable[r];
        Flags.Carry = carry;
        // N is left as the previous op set it.
        _flagsModified = true;
        AF.A = r;
    }

    // CPL: A = ~A; sets H and N, copies bits 5/3 from the new A, leaves the rest.
    private void Cpl()
    {
        AF.A = (byte)~AF.A;
        Flags.HalfCarry = true;
        Flags.Subtract = true;
        Flags.Y = (AF.A & 0x20) != 0;
        Flags.X = (AF.A & 0x08) != 0;
        _flagsModified = true;
    }

    private void Scf()
    {
        var f = Flags.AsByte();
        Flags.HalfCarry = false;
        Flags.Subtract = false;
        Flags.Carry = true;
        SetScfCcfUndocumentedBits(f);
    }

    private void Ccf()
    {
        var f = Flags.AsByte();
        Flags.HalfCarry = Flags.Carry;
        Flags.Subtract = false;
        Flags.Carry = !Flags.Carry;
        SetScfCcfUndocumentedBits(f);
    }

    // NMOS Zilog SCF / CCF: bits 5 and 3 of F become ((Q ^ F) | A) at those
    // positions, where Q is the F byte the previous instruction produced (0 if
    // it did not write F) and F is the value in force before this SCF / CCF.
    private void SetScfCcfUndocumentedBits(byte fBefore)
    {
        var bits = (_q ^ fBefore) | AF.A;
        Flags.Y = (bits & 0x20) != 0;
        Flags.X = (bits & 0x08) != 0;
        _flagsModified = true;
    }

    // ADD HL,ss / ADD IX,ss / ADD IY,ss: H and C come from bit 11 / bit 15, Y/X
    // from the high byte of the result, N is cleared; S, Z and P/V are
    // untouched. WZ = dst + 1, latched before the sum. The destination is HL for
    // the unprefixed opcode and the active index register for a DD/FD-prefixed
    // one (in which case ss's "HL" slot is that same index register).
    private void Add16(ref ushort dst, ushort src)
    {
        var start = dst;
        WZ.Value = (ushort)(start + 1);

        var result = start + src;
        var r = (ushort)result;

        Flags.HalfCarry = ((start & 0x0FFF) + (src & 0x0FFF)) > 0x0FFF;
        Flags.Carry = result > 0xFFFF;
        Flags.Subtract = false;
        Flags.Y = (r & 0x2000) != 0;
        Flags.X = (r & 0x0800) != 0;
        _flagsModified = true;

        dst = r;
    }

    // Accumulator rotates RLCA / RRCA / RLA / RRA: H and N cleared, C from the
    // bit rotated out, bits 5/3 from the rotated A; S, Z and P/V untouched.
    private void Rlca()
    {
        var carry = (AF.A & 0x80) != 0;
        AF.A = (byte)((AF.A << 1) | (carry ? 1 : 0));
        SetAccumulatorRotateFlags(carry);
    }

    private void Rrca()
    {
        var carry = (AF.A & 0x01) != 0;
        AF.A = (byte)((AF.A >> 1) | (carry ? 0x80 : 0));
        SetAccumulatorRotateFlags(carry);
    }

    private void Rla()
    {
        var carry = (AF.A & 0x80) != 0;
        AF.A = (byte)((AF.A << 1) | (Flags.Carry ? 1 : 0));
        SetAccumulatorRotateFlags(carry);
    }

    private void Rra()
    {
        var carry = (AF.A & 0x01) != 0;
        AF.A = (byte)((AF.A >> 1) | (Flags.Carry ? 0x80 : 0));
        SetAccumulatorRotateFlags(carry);
    }

    private void SetAccumulatorRotateFlags(bool carry)
    {
        Flags.Carry = carry;
        Flags.HalfCarry = false;
        Flags.Subtract = false;
        Flags.Y = (AF.A & 0x20) != 0;
        Flags.X = (AF.A & 0x08) != 0;
        _flagsModified = true;
    }

    // --- CB-prefixed rotates and shifts -------------------------------------
    //
    // Unlike the accumulator rotates (RLCA/RRCA/RLA/RRA), the CB rotate/shift
    // group sets S, Z and P/V from the result and takes bits 5/3 from it too;
    // H and N are cleared and C is the bit shifted out. rot[y]: 0 RLC, 1 RRC,
    // 2 RL, 3 RR, 4 SLA, 5 SRA, 6 SLL, 7 SRL (z80.info decode). SLL (a.k.a.
    // SL1) is undocumented: a left shift that feeds a 1 into bit 0.
    private byte AluRotateShift(int op, byte v)
    {
        bool carryOut;
        byte r;

        switch (op)
        {
            case 0: // RLC
                carryOut = (v & 0x80) != 0;
                r = (byte)((v << 1) | (carryOut ? 1 : 0));
                break;
            case 1: // RRC
                carryOut = (v & 0x01) != 0;
                r = (byte)((v >> 1) | (carryOut ? 0x80 : 0));
                break;
            case 2: // RL
                carryOut = (v & 0x80) != 0;
                r = (byte)((v << 1) | (Flags.Carry ? 1 : 0));
                break;
            case 3: // RR
                carryOut = (v & 0x01) != 0;
                r = (byte)((v >> 1) | (Flags.Carry ? 0x80 : 0));
                break;
            case 4: // SLA
                carryOut = (v & 0x80) != 0;
                r = (byte)(v << 1);
                break;
            case 5: // SRA - arithmetic: bit 7 is replicated
                carryOut = (v & 0x01) != 0;
                r = (byte)((v >> 1) | (v & 0x80));
                break;
            case 6: // SLL - undocumented: bit 0 becomes 1
                carryOut = (v & 0x80) != 0;
                r = (byte)((v << 1) | 1);
                break;
            default: // 7: SRL
                carryOut = (v & 0x01) != 0;
                r = (byte)(v >> 1);
                break;
        }

        Flags.Sign = (r & 0x80) != 0;
        Flags.Zero = r == 0;
        Flags.Y = (r & 0x20) != 0;
        Flags.HalfCarry = false;
        Flags.X = (r & 0x08) != 0;
        Flags.ParityOverflow = ParityTable[r];
        Flags.Subtract = false;
        Flags.Carry = carryOut;
        _flagsModified = true;
        return r;
    }

    // --- CB-prefixed BIT / RES / SET --------------------------------------
    //
    // BIT tests one bit: Z and P/V both reflect the bit being clear, H is set,
    // N cleared, C untouched. S is only meaningful for bit 7 (it takes the
    // tested bit). Bits 5/3 come from the operand for BIT b,r; for BIT b,(HL)
    // they come from the high byte of MEMPTR, which the instruction leaves
    // unchanged ("The Undocumented Z80 Documented", Sean Young).
    private void AluBit(int bit, byte v, byte undocumentedSource)
    {
        var masked = (byte)(v & (1 << bit));

        Flags.Sign = (masked & 0x80) != 0;
        Flags.Zero = masked == 0;
        Flags.ParityOverflow = masked == 0;
        Flags.HalfCarry = true;
        Flags.Subtract = false;
        Flags.Y = (undocumentedSource & 0x20) != 0;
        Flags.X = (undocumentedSource & 0x08) != 0;
        _flagsModified = true;
    }

    // --- NEG (ED 44 and its undocumented mirrors) -----------------------
    //
    // A := 0 - A, with the flags of a subtraction from zero: H and C are the
    // borrows, N is set, P/V is set only when A was 0x80, C only when A was
    // non-zero.
    private void Neg()
    {
        AF.A = Alu8Sub(0, AF.A, 0);
    }

    // --- ED-prefixed 16-bit ADC HL,ss / SBC HL,ss --------------------------
    //
    // Full flags, unlike ADD HL,ss: S and Z from the 16-bit result, H from a
    // carry/borrow out of bit 11, P/V is signed overflow, N is 0 for ADC and 1
    // for SBC, C from bit 15, and bits 5/3 from the result's high byte. WZ is
    // latched to HL + 1 before the operation.
    private void Adc16ToHl(ushort src)
    {
        var hl = HL.Value;
        WZ.Value = (ushort)(hl + 1);

        var carryIn = Flags.Carry ? 1 : 0;
        var result = hl + src + carryIn;
        var r = (ushort)result;

        Flags.Sign = (r & 0x8000) != 0;
        Flags.Zero = r == 0;
        Flags.Y = (r & 0x2000) != 0;
        Flags.HalfCarry = ((hl & 0x0FFF) + (src & 0x0FFF) + carryIn) > 0x0FFF;
        Flags.X = (r & 0x0800) != 0;
        Flags.ParityOverflow = ((hl ^ src ^ 0x8000) & (hl ^ r) & 0x8000) != 0;
        Flags.Subtract = false;
        Flags.Carry = result > 0xFFFF;
        _flagsModified = true;

        HL.Value = r;
    }

    private void Sbc16FromHl(ushort src)
    {
        var hl = HL.Value;
        WZ.Value = (ushort)(hl + 1);

        var carryIn = Flags.Carry ? 1 : 0;
        var result = hl - src - carryIn;
        var r = (ushort)result;

        Flags.Sign = (r & 0x8000) != 0;
        Flags.Zero = r == 0;
        Flags.Y = (r & 0x2000) != 0;
        Flags.HalfCarry = ((hl & 0x0FFF) - (src & 0x0FFF) - carryIn) < 0;
        Flags.X = (r & 0x0800) != 0;
        Flags.ParityOverflow = ((hl ^ src) & (hl ^ r) & 0x8000) != 0;
        Flags.Subtract = true;
        Flags.Carry = result < 0;
        _flagsModified = true;

        HL.Value = r;
    }

    // --- ED-prefixed RRD / RLD ------------------------------------------
    //
    // A nibble rotate through (HL) and the low nibble of A. RRD shifts the
    // 12-bit quantity [A.lo : (HL)] right by four; RLD shifts it left by four.
    // S, Z, P/V (parity) and bits 5/3 come from A afterwards; H and N are
    // cleared, C is untouched. WZ is set to HL + 1.
    private byte Rrd(byte memory)
    {
        WZ.Value = (ushort)(HL.Value + 1);
        var newMemory = (byte)((AF.A << 4) | (memory >> 4));
        AF.A = (byte)((AF.A & 0xF0) | (memory & 0x0F));
        SetRrdRldFlags();
        return newMemory;
    }

    private byte Rld(byte memory)
    {
        WZ.Value = (ushort)(HL.Value + 1);
        var newMemory = (byte)((memory << 4) | (AF.A & 0x0F));
        AF.A = (byte)((AF.A & 0xF0) | (memory >> 4));
        SetRrdRldFlags();
        return newMemory;
    }

    // LD A,I / LD A,R: S and Z from the byte loaded, bits 5/3 from it too, H
    // and N cleared, C untouched, and P/V loaded from IFF2. (A maskable
    // interrupt landing on the internal T-state of this instruction resets P/V
    // instead - that corner is handled with the interrupt sequences.)
    private void SetLdAInterruptRegisterFlags(byte value)
    {
        Flags.Sign = (value & 0x80) != 0;
        Flags.Zero = value == 0;
        Flags.Y = (value & 0x20) != 0;
        Flags.HalfCarry = false;
        Flags.X = (value & 0x08) != 0;
        Flags.ParityOverflow = IFF2;
        Flags.Subtract = false;
        _flagsModified = true;
    }

    private void SetRrdRldFlags()
    {
        Flags.Sign = (AF.A & 0x80) != 0;
        Flags.Zero = AF.A == 0;
        Flags.Y = (AF.A & 0x20) != 0;
        Flags.HalfCarry = false;
        Flags.X = (AF.A & 0x08) != 0;
        Flags.ParityOverflow = ParityTable[AF.A];
        Flags.Subtract = false;
        _flagsModified = true;
    }

    // --- ED-prefixed IN r,(C) / IN (C) --------------------------------
    //
    // S, Z and P/V (parity) come from the byte read; H and N are cleared and C
    // is untouched. The same flag rule serves IN (C) (the y == 6 form) which
    // discards the byte and only writes the flags.
    private void SetInputFlags(byte value)
    {
        Flags.Sign = (value & 0x80) != 0;
        Flags.Zero = value == 0;
        Flags.Y = (value & 0x20) != 0;
        Flags.HalfCarry = false;
        Flags.X = (value & 0x08) != 0;
        Flags.ParityOverflow = ParityTable[value];
        Flags.Subtract = false;
        _flagsModified = true;
    }

    // --- ED-prefixed block-transfer / block-compare flags ---------------
    //
    // LDI/LDD/LDIR/LDDR: after the byte moves, P/V is set while BC is still
    // non-zero, H and N are cleared, and - the undocumented part - bits 5/3
    // come from A plus the moved byte: bit 1 of that sum lands in flag Y (bit
    // 5) and bit 3 in flag X. S, Z and C are left alone. ("The Undocumented
    // Z80 Documented", ch. 4.)
    private void SetBlockLoadFlags(byte moved, bool bcStillNonZero)
    {
        var n = (byte)(AF.A + moved);
        Flags.Y = (n & 0x02) != 0;
        Flags.HalfCarry = false;
        Flags.X = (n & 0x08) != 0;
        Flags.ParityOverflow = bcStillNonZero;
        Flags.Subtract = false;
        _flagsModified = true;
    }

    // CPI/CPD/CPIR/CPDR: a compare of A against (HL) that does not store. S, Z
    // and H are the compare's, N is set, P/V tracks BC still non-zero, C is
    // untouched. Bits 5/3 come from (A - (HL) - H): bit 1 of that into flag Y,
    // bit 3 into flag X.
    private void SetBlockCompareFlags(byte memory, bool bcStillNonZero)
    {
        var diff = AF.A - memory;
        var r = (byte)diff;
        var halfBorrow = ((AF.A & 0x0F) - (memory & 0x0F)) < 0;

        Flags.Sign = (r & 0x80) != 0;
        Flags.Zero = r == 0;
        Flags.HalfCarry = halfBorrow;
        Flags.ParityOverflow = bcStillNonZero;
        Flags.Subtract = true;

        var n = (byte)(r - (halfBorrow ? 1 : 0));
        Flags.Y = (n & 0x02) != 0;
        Flags.X = (n & 0x08) != 0;
        _flagsModified = true;
    }

    // INI/IND/OUTI/OUTD and their repeating forms. The final iteration's flags
    // are the observable ones (each repeat overwrites F): S, Z and bits 5/3
    // follow B after its decrement, N is bit 7 of the transferred byte, and a
    // synthetic sum k = byte + kAddend drives H and C (k > 0xFF) while P/V is
    // the parity of (k & 7) XOR B. For IN* kAddend is (C +/- 1); for OUT* it is
    // the post-step value of L. ("The Undocumented Z80 Documented", ch. 5.)
    private void SetBlockIoFlags(byte transferred, byte b, int kAddend)
    {
        var k = transferred + (kAddend & 0xFF);

        Flags.Sign = (b & 0x80) != 0;
        Flags.Zero = b == 0;
        Flags.Y = (b & 0x20) != 0;
        Flags.X = (b & 0x08) != 0;
        Flags.HalfCarry = k > 0xFF;
        Flags.Carry = k > 0xFF;
        Flags.ParityOverflow = ParityTable[(k & 0x07) ^ b];
        Flags.Subtract = (transferred & 0x80) != 0;
        _flagsModified = true;
    }

    private bool EvaluateCondition(int cc)
    {
        return cc switch
        {
            0 => !Flags.Zero,
            1 => Flags.Zero,
            2 => !Flags.Carry,
            3 => Flags.Carry,
            4 => !Flags.ParityOverflow,
            5 => Flags.ParityOverflow,
            6 => !Flags.Sign,
            7 => Flags.Sign,
            _ => throw new System.InvalidOperationException(),
        };
    }
}
