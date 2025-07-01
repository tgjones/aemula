using System.Runtime.CompilerServices;

namespace Aemula.Emulation.Chips.Mos6502;

public struct ProcessorFlags
{
    /// <summary>
    /// Carry
    /// </summary>
    public bool C;

    /// <summary>
    /// Zero
    /// </summary>
    public bool Z;

    /// <summary>
    /// Interrupt disable
    /// </summary>
    public bool I;

    /// <summary>
    /// Binary coded decimal
    /// </summary>
    public bool D;

    /// <summary>
    /// Overflow
    /// </summary>
    public bool V;

    /// <summary>
    /// Negative
    /// </summary>
    public bool N;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetFromByte(byte value)
    {
        C = (value & 0x01) == 0x01;
        Z = (value & 0x02) == 0x02;
        I = (value & 0x04) == 0x04;
        D = (value & 0x08) == 0x08;
        V = (value & 0x40) == 0x40;
        N = (value & 0x80) == 0x80;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public byte SetZeroNegativeFlags(byte value)
    {
        Z = value == 0;
        N = (value & 0x80) == 0x80;

        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte AsByte(bool bit4Set)
    {
        var result = 0;
        if (C)
        {
            result |= 0x01;
        }
        if (Z)
        {
            result |= 0x02;
        }
        if (I)
        {
            result |= 0x04;
        }
        if (D)
        {
            result |= 0x08;
        }
        if (bit4Set) // Bit 4 is 1 if being pushed to stack by an instruction (PHP or BRK).
        {
            result |= 0x10;
        }
        result |= 0x20; // Bit 5 is always 1.
        if (V)
        {
            result |= 0x40;
        }
        if (N)
        {
            result |= 0x80;
        }
        return (byte)result;
    }
}
