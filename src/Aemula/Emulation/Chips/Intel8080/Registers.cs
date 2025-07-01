using System.Runtime.InteropServices;

namespace Aemula.Emulation.Chips.Intel8080;

[StructLayout(LayoutKind.Explicit)]
public struct BCRegister
{
    [FieldOffset(0)]
    public ushort Value;

    [FieldOffset(0)]
    public byte C;

    [FieldOffset(1)]
    public byte B;
}

[StructLayout(LayoutKind.Explicit)]
public struct DERegister
{
    [FieldOffset(0)]
    public ushort Value;

    [FieldOffset(0)]
    public byte E;

    [FieldOffset(1)]
    public byte D;
}

[StructLayout(LayoutKind.Explicit)]
public struct HLRegister
{
    [FieldOffset(0)]
    public ushort Value;

    [FieldOffset(0)]
    public byte L;

    [FieldOffset(1)]
    public byte H;
}

[StructLayout(LayoutKind.Explicit)]
public struct SPRegister
{
    [FieldOffset(0)]
    public ushort Value;

    [FieldOffset(0)]
    public byte Lo;

    [FieldOffset(1)]
    public byte Hi;
}

[StructLayout(LayoutKind.Explicit)]
public struct WZRegister
{
    [FieldOffset(0)]
    public ushort Value;

    [FieldOffset(0)]
    public byte Z;

    [FieldOffset(1)]
    public byte W;
}

[StructLayout(LayoutKind.Explicit)]
public struct PCRegister
{
    [FieldOffset(0)]
    public ushort Value;

    [FieldOffset(0)]
    public byte Lo;

    [FieldOffset(1)]
    public byte Hi;
}
