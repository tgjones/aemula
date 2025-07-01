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
}
