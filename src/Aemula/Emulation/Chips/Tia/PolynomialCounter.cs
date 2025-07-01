using static Aemula.BitUtility;

namespace Aemula.Emulation.Chips.Tia;

/// <summary>
/// 6-bit counter value.
/// </summary>
internal struct PolynomialCounter
{
    public byte Value { get; private set; }

    public void Increment()
    {
        // Put high 5 bits from old value into low 5 bits in new value.
        var newLoBits = Value >> 1 & 0b11111;

        // New high bit is 1 if the low 2 bits are the same, and 0 if they are different.
        var newHiBit = GetBit(Value, 0) == GetBit(Value, 1) ? 1 : 0;

        Value = (byte)(newHiBit << 5 | newLoBits);
    }

    public void Reset()
    {
        Value = 0;
    }
}
