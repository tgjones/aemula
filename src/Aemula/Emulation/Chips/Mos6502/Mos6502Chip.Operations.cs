// This file contains methods for the more complex opcodes.
// Simpler operations are generated inline in Mos6502Chip.generated.cs.

using System.Runtime.CompilerServices;

namespace Aemula.Emulation.Chips.Mos6502;

partial class Mos6502Chip
{
    /// <summary>
    /// AND - Logical AND
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void And(byte data)
    {
        A = P.SetZeroNegativeFlags((byte)(A & data));
    }

    private void Arr(in Mos6502Pins pins)
    {
        And(pins.Data);

        // http://www.6502.org/users/andre/petindex/local/64doc.txt
        if (_bcdEnabled && P.D)
        {
            // Do ROR.
            var a = (byte)(A >> 1);

            // Add carry flag to MSB.
            if (P.C)
            {
                a |= 0x80;
            }

            // Set zero and negative flags as normal.
            P.SetZeroNegativeFlags(a);

            // The V flag will be set if the bit 6 of the accumulator changed its state
            // between the AND and the ROR, cleared otherwise.
            P.V = ((A ^ a) & 0x40) != 0;

            // Now it gets weird: if low nibble is greater than 5, increment it by 6,
            // but if it carries, don't add it to high nibble.
            if ((A & 0xF) >= 5)
            {
                a = (byte)(((a + 6) & 0xF) | (a & 0xF0));
            }

            // If high nibble is greater than 5, increment it by 6,
            // and set the carry flag.
            if ((A & 0xF0) >= 0x50)
            {
                a += 0x60;
                P.C = true;
            }
            else
            {
                P.C = false;
            }

            A = a;
        }
        else
        {
            Rora();

            // The C flag is copied from bit 6 of the result.
            P.C = (A & 0x40) != 0;

            // The V flag is the result of an XOR operation between bit 6 and bit 5 of the result.
            P.V = (((A & 0x40) >> 6) ^ ((A & 0x20) >> 5)) == 1;
        }
    }

    private void DoAdcBinary(byte value)
    {
        var temp = (ushort)(A + value + (P.C ? 1 : 0));
        P.V = ((A ^ temp) & (value ^ temp) & 0x80) == 0x80;
        P.C = temp > 0xFF;
        A = P.SetZeroNegativeFlags((byte)temp);
    }

    private void DoAdcDecimal(byte value)
    {
        var temp = (byte)((A + value + (P.C ? 1 : 0)) & 0xFF);
        P.Z = temp == 0;

        var ah = 0;
        var al = (A & 0xF) + (value & 0xF) + (P.C ? 1 : 0);
        if (al > 9)
        {
            al -= 10;
            al &= 0xF;
            ah = 1;
        }

        ah += (A >> 4) + (value >> 4);
        P.N = (ah & 8) == 8;
        P.V = ((A ^ value) & 0x80) == 0 && ((A ^ (ah << 4)) & 0x80) == 0x80;
        P.C = false;

        if (ah > 9)
        {
            P.C = true;
            ah -= 10;
            ah &= 0xF;
        }

        A = (byte)(((al & 0xF) | (ah << 4)) & 0xFF);
    }

    /// <summary>
    /// ADC - Add with Carry
    /// </summary>
    private void Adc(byte data)
    {
        if (!P.D || !_bcdEnabled)
        {
            DoAdcBinary(data);
        }
        else
        {
            DoAdcDecimal(data);
        }
    }

    private void DoSbcDecimal(byte value)
    {
        var carry = P.C ? 0 : 1;
        var al = (A & 0xF) - (value & 0xF) - carry;
        var ah = (A >> 4) - (value >> 4);

        if ((al & 0x10) == 0x10)
        {
            al = (al - 6) & 0xF;
            ah--;
        }

        if ((ah & 0x10) == 0x10)
        {
            ah = (ah - 6) & 0xF;
        }

        var result = A - value - carry;
        P.N = (result & 0x80) == 0x80;
        P.Z = (result & 0xFF) == 0;
        P.V = ((A ^ result) & (value ^ A) & 0x80) == 0x80;
        P.C = (result & 0x100) == 0;
        A = (byte)(al | (ah << 4));
    }

    /// <summary>
    /// SBC - Subtract with Carry
    /// </summary>
    private void Sbc(byte data)
    {
        if (!P.D || !_bcdEnabled)
        {
            DoAdcBinary((byte)~data);
        }
        else
        {
            DoSbcDecimal(data);
        }
    }

    private byte AslHelper(byte value)
    {
        P.C = (value & 0x80) == 0x80;
        return P.SetZeroNegativeFlags((byte)(value << 1));
    }

    private byte LsrHelper(byte value)
    {
        P.C = (value & 0x1) == 0x1;
        return P.SetZeroNegativeFlags((byte)(value >> 1));
    }

    private byte RolHelper(byte value)
    {
        var temp = (byte)(P.C ? 1 : 0);
        P.C = (value & 0x80) == 0x80;
        return P.SetZeroNegativeFlags((byte)((value << 1) | temp));
    }

    private byte RorHelper(byte value)
    {
        var temp = (byte)((P.C ? 1 : 0) << 7);
        P.C = (value & 0x1) == 0x1;
        return P.SetZeroNegativeFlags((byte)((value >> 1) | temp));
    }

    /// <summary>
    /// ROR - Rotate Right (accumulator addressing)
    /// </summary>
    private void Rora()
    {
        A = RorHelper(A);
    }

    /// <summary>
    /// Increments the timing register (which has the effect of skipping the next instruction cycle)
    /// if no page boundary is crossed when <paramref name="addend"/> is added to <see cref="_ad"/>.
    /// 
    /// This implementation goes to some effort to avoid branching.
    ///
    /// Thanks to Andre Weissflog's code at
    /// https://github.com/floooh/chips/blob/fdef73e0e65ebb5aa2bf33e226ba5b108a7a43fb/codegen/m6502_gen.py#L208
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void IncrementTimingRegisterIfNoPageCrossing(byte addend)
    {
        // Original high-byte
        var original = _ad >> 8;

        // High-byte after adding value
        var modified = (_ad + addend) >> 8;

        // Delta will be either 0 (if it didn't cross page) or -1 (if it did).
        var delta = original - modified;

        // Increment will be either 1 (if it didn't cross page) or 0 (if it did).
        var timingRegisterIncrement = ~delta & 1;

        _tr += (byte)timingRegisterIncrement;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private void FetchNextInstruction(ref Mos6502Pins pins)
    {
        pins.Address = PC;
        pins.Sync = true;
    }
}
