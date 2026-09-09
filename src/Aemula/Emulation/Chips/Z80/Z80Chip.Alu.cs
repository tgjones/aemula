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

    // ADD HL,ss: H and C come from bit 11 / bit 15, Y/X from the high byte of
    // the result, N is cleared; S, Z and P/V are untouched. WZ = HL + 1, latched
    // before the sum.
    private void Add16ToHl(ushort src)
    {
        var hl = HL.Value;
        WZ.Value = (ushort)(hl + 1);

        var result = hl + src;
        var r = (ushort)result;

        Flags.HalfCarry = ((hl & 0x0FFF) + (src & 0x0FFF)) > 0x0FFF;
        Flags.Carry = result > 0xFFFF;
        Flags.Subtract = false;
        Flags.Y = (r & 0x2000) != 0;
        Flags.X = (r & 0x0800) != 0;
        _flagsModified = true;

        HL.Value = r;
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
