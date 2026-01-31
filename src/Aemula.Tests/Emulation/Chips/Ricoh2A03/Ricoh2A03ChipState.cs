using Aemula.Tests.Emulation.Chips.Mos6502;

namespace Aemula.Tests.Emulation.Chips.Ricoh2A03;

internal record struct Ricoh2A03PinState(
    bool Clk,
    bool M2,
    bool CorePhi2, // Not actually exposed on external pin, but helpful for testing
    ushort Address,
    byte Data,
    bool RW,
    bool CoreSync, // Not actually exposed on external pin, but helpful for testing
    bool Rst)
{
    public override readonly string ToString()
    {
        return $"Clock {(Clk ? '1' : '0')}/{(M2 ? '1' : '0')}/{(CorePhi2 ? '1' : '0')}   AB {Address:X4}   DB {Data:X2}   RW {(RW ? '1' : '0')}   SYNC {(CoreSync ? '1' : '0')}   RST {(Rst ? '1' : '0')}";
    }
}

internal record struct Ricoh2A03RegisterState(
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

