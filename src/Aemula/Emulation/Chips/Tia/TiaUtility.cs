using static Aemula.BitUtility;

namespace Aemula.Emulation.Chips.Tia;

internal static class TiaUtility
{
    public static bool NoneEqual(byte lhs, byte rhs)
    {
        var result = 1;

        result &= GetBit(lhs, 0) ^ GetBit(rhs, 0);
        result &= GetBit(lhs, 1) ^ GetBit(rhs, 1);
        result &= GetBit(lhs, 2) ^ GetBit(rhs, 2);
        result &= GetBit(lhs, 3) ^ GetBit(rhs, 3);

        return result == 1;
    }
}
