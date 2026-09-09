namespace Aemula.Emulation.Chips.Z80;

/// <summary>
/// The Z80 flag register (F), bits 7..0: S Z Y H X P/V N C. Every bit is real -
/// unlike the 8080's F, none are forced.
///
/// <list type="bullet">
///   <item>S   (bit 7) - sign: a copy of the result's bit 7.</item>
///   <item>Z   (bit 6) - zero: set when the result is 0.</item>
///   <item>Y   (bit 5) - undocumented: usually a copy of the result's bit 5,
///         but BIT n,(HL) / SCF / CCF / the block ops follow their own rules.</item>
///   <item>H   (bit 4) - half carry: carry / borrow out of bit 3, consumed by DAA.</item>
///   <item>X   (bit 3) - undocumented: usually a copy of the result's bit 3, with
///         the same special cases as Y.</item>
///   <item>P/V (bit 2) - parity for logical / rotate / IN r,(C); signed overflow
///         for arithmetic.</item>
///   <item>N   (bit 1) - set when the last ALU operation was a subtraction (DAA
///         reads it to pick its correction direction).</item>
///   <item>C   (bit 0) - carry / borrow out of bit 7.</item>
/// </list>
/// </summary>
public struct Z80Flags
{
    public bool Sign;
    public bool Zero;
    public bool Y;
    public bool HalfCarry;
    public bool X;
    public bool ParityOverflow;
    public bool Subtract;
    public bool Carry;

    public byte AsByte()
    {
        byte result = 0;
        if (Carry)
        {
            result |= 0x01;
        }
        if (Subtract)
        {
            result |= 0x02;
        }
        if (ParityOverflow)
        {
            result |= 0x04;
        }
        if (X)
        {
            result |= 0x08;
        }
        if (HalfCarry)
        {
            result |= 0x10;
        }
        if (Y)
        {
            result |= 0x20;
        }
        if (Zero)
        {
            result |= 0x40;
        }
        if (Sign)
        {
            result |= 0x80;
        }
        return result;
    }

    public void SetFromByte(byte value)
    {
        Carry = (value & 0x01) != 0;
        Subtract = (value & 0x02) != 0;
        ParityOverflow = (value & 0x04) != 0;
        X = (value & 0x08) != 0;
        HalfCarry = (value & 0x10) != 0;
        Y = (value & 0x20) != 0;
        Zero = (value & 0x40) != 0;
        Sign = (value & 0x80) != 0;
    }
}
