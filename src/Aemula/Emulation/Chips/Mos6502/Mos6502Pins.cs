namespace Aemula.Chips.Mos6502;

public struct Mos6502Pins
{
    public ushort Address;
    public byte Data;
    public bool Rdy;
    public bool Irq;
    public bool Nmi;
    public bool Sync;
    public bool Res;

    /// <summary>
    /// Read/write pin. True for read, false for write.
    /// </summary>
    public bool RW;
}
