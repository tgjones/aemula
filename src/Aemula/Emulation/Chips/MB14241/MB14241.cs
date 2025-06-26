namespace Aemula.Chips.MB14241;

public sealed class MB14241
{
    // Only 15 bits are used.
    private ushort _shiftData;

    // Only 3 bits are used.
    private byte _shiftCount;

    public void SetShiftCount(byte value)
    {
        _shiftCount = (byte)(~value & 0b111);
    }

    public void SetShiftData(byte value)
    {
        _shiftData = (ushort)((_shiftData >> 8) | (value << 7));
    }

    public byte GetResult()
    {
        return (byte)(_shiftData >> _shiftCount);
    }
}
