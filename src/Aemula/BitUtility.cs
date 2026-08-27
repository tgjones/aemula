namespace Aemula;

public static class BitUtility
{
    public static byte GetBit(byte value, int position)
    {
        return (byte)(value >> position & 1);
    }

    public static bool GetBitAsBoolean(byte value, int position)
    {
        return GetBit(value, position) != 0;
    }

    public static byte GetBit(ushort value, int position)
    {
        return (byte)(value >> position & 1);
    }

    public static bool GetBitAsBoolean(ushort value, int position)
    {
        return GetBit(value, position) != 0;
    }

    public static byte GetBit(uint value, int position)
    {
        return (byte)(value >> position & 1);
    }

    public static bool GetBitAsBoolean(uint value, int position)
    {
        return GetBit(value, position) != 0;
    }

    public static void ClearBit(ref byte value, int position)
    {
        value = (byte)(value & ~(1 << position));
    }
}
