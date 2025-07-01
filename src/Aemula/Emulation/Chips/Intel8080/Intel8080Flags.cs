namespace Aemula.Emulation.Chips.Intel8080;

public struct Intel8080Flags
{
    public bool Zero;
    public bool Carry;
    public bool AuxiliaryCarry;
    public bool Sign;
    public bool Parity;

    public byte AsByte()
    {
        byte result = 0;
        if (Carry)
        {
            result |= 0x1;
        }
        result |= 0x2; // Bit 1 is always 1
        if (Parity)
        {
            result |= 0x4;
        }
        // Bit 3 is always 0
        if (AuxiliaryCarry)
        {
            result |= 0x10;
        }
        // Bit 5 is always 0
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
        Carry = (value & 0x1) != 0;
        Parity = (value & 0x4) != 0;
        AuxiliaryCarry = (value & 0x10) != 0;
        Zero = (value & 0x40) != 0;
        Sign = (value & 0x80) != 0;
    }
}
