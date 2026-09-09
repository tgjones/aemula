using System.Runtime.InteropServices;

namespace Aemula.Emulation.Chips.Z80;

public sealed partial class Z80Chip
{
    // Main register set. AF/BC/DE/HL are addressable as a 16-bit pair or as two
    // 8-bit halves. IX/IY likewise, including the undocumented IXH/IXL/IYH/IYL
    // halves the DD/FD-prefixed opcodes expose.
    public AFRegister AF;
    public BCRegister BC;
    public DERegister DE;
    public HLRegister HL;
    public IXRegister IX;
    public IYRegister IY;
    public SPRegister SP;
    public PCRegister PC;

    // WZ, a.k.a. MEMPTR: an internal 16-bit scratch pair with no direct opcode
    // access. It is observable only through the undocumented flag results of
    // BIT n,(HL) / BIT n,(IX+d) and of SCF/CCF, so it is modelled from the start
    // rather than retrofitted.
    public WZRegister WZ;

    // The unpacked flag register. AF.F is its packed mirror - microcode keeps the
    // two in sync so PUSH AF / POP AF / EX AF,AF' see the byte form while the ALU
    // works on the fields.
    public Z80Flags Flags;

    // Alternate register set, swapped in whole by EX AF,AF' (the AF' pair) and
    // EXX (BC'/DE'/HL'). Nothing addresses their 8-bit halves, so they are plain
    // 16-bit values.
    public ushort AFalt;
    public ushort BCalt;
    public ushort DEalt;
    public ushort HLalt;

    // I - interrupt vector page: the high byte of the address the CPU forms in
    // interrupt mode 2 to fetch a handler pointer.
    public byte I;

    // R - memory refresh counter. Bits 0-6 auto-increment on every M1 opcode
    // fetch; bit 7 survives that increment untouched but is fully writable by
    // LD R,A.
    public byte R;

    // Interrupt enable flip-flops. IFF1 gates /INT. IFF2 holds a copy of IFF1
    // across an NMI (RETN copies it back) and is what LD A,I / LD A,R read into
    // the parity/overflow flag.
    public bool IFF1;
    public bool IFF2;

    // Interrupt mode: 0, 1 or 2.
    public byte IM;
}

/// <summary>AF - accumulator (A, high byte) and flags (F, low byte).</summary>
[StructLayout(LayoutKind.Explicit)]
public struct AFRegister
{
    [FieldOffset(0)]
    public ushort Value;

    [FieldOffset(0)]
    public byte F;

    [FieldOffset(1)]
    public byte A;
}

/// <summary>BC - B (high byte), C (low byte).</summary>
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

/// <summary>DE - D (high byte), E (low byte).</summary>
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

/// <summary>HL - H (high byte), L (low byte).</summary>
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

/// <summary>
/// IX - index register, with the undocumented IXH (high byte) / IXL (low byte)
/// halves that DD-prefixed opcodes address.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
public struct IXRegister
{
    [FieldOffset(0)]
    public ushort Value;

    [FieldOffset(0)]
    public byte IXL;

    [FieldOffset(1)]
    public byte IXH;
}

/// <summary>
/// IY - index register, with the undocumented IYH (high byte) / IYL (low byte)
/// halves that FD-prefixed opcodes address.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
public struct IYRegister
{
    [FieldOffset(0)]
    public ushort Value;

    [FieldOffset(0)]
    public byte IYL;

    [FieldOffset(1)]
    public byte IYH;
}

/// <summary>SP - stack pointer, with Hi / Lo byte access.</summary>
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

/// <summary>PC - program counter, with Hi / Lo byte access.</summary>
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

/// <summary>WZ / MEMPTR - internal scratch pair, with W (high byte) / Z (low byte).</summary>
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
