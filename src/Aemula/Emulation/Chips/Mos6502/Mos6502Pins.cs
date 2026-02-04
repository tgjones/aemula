namespace Aemula.Emulation.Chips.Mos6502;

public struct Mos6502Pins
{
    public bool Rdy;

    /// <summary>
    /// Read/write pin. True for read, false for write.
    /// </summary>
    public bool RW;
}
