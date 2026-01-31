using Aemula.Emulation.Chips.Mos6502;

namespace Aemula.Tests.Emulation.Chips.Mos6502;

internal record struct Mos6502PinState(
    bool Phi2,
    ushort Address,
    byte Data,
    bool RW,
    bool Sync,
    bool Res)
{
    public override readonly string ToString()
    {
        return $"Ø2 {(Phi2 ? '1' : '0')}   AB {Address:X4}   DB {Data:X2}   RW {(RW ? '1' : '0')}   SYNC {(Sync ? '1' : '0')}   RES {(Res ? '1' : '0')}";
    }
}

internal record struct Mos6502RegisterState(
    ushort PC,
    byte A,
    byte X,
    byte Y,
    byte SP,
    Mos6502ProcessorFlagsState P)
{
    public override readonly string ToString()
    {
        return $"PC {PC:X4}   A {A:X2}   X {X:X2}   Y {Y:X2}   SP {SP:X2}   P {P}";
    }
}

internal record struct Mos6502ProcessorFlagsState(
    bool C,
    bool Z,
    bool I,
    bool D,
    bool V,
    bool N,
    byte Raw)
{
    public static Mos6502ProcessorFlagsState FromProcessorFlags(ProcessorFlags flags) => new(
        flags.C,
        flags.Z,
        flags.I,
        flags.D,
        flags.V,
        flags.N,
        flags.AsByte(false));

    public static Mos6502ProcessorFlagsState FromByte(byte value)
    {
        var flags = new ProcessorFlags();
        flags.SetFromByte(value);
        return FromProcessorFlags(flags);
    }

    public override readonly string ToString()
    {
        return $"{(N ? 'N' : 'n')}{(V ? 'V' : 'v')}xb{(D ? 'D' : 'd')}{(I ? 'I' : 'i')}{(Z ? 'Z' : 'z')}{(C ? 'C' : 'c')}";
    }
}
