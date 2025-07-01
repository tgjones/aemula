using System;

namespace Aemula.Debugging;

public readonly struct DebuggerMemoryCallbacks
{
    public readonly Func<ushort, byte> Read;
    public readonly Action<ushort, byte> Write;

    public DebuggerMemoryCallbacks(
        Func<ushort, byte> read,
        Action<ushort, byte> write)
    {
        Read = read;
        Write = write;
    }

    public ushort ReadWord(ushort address)
    {
        var lo = Read(address);
        var hi = Read((ushort)(address + 1));
        return (ushort)(hi << 8 | lo);
    }
}
