namespace Aemula.Emulation.Chips.Mos6532;

public struct Mos6532Pins
{
    /// <summary>
    /// Reset pin.
    /// </summary>
    public bool Res;

    /// <summary>
    /// Read/write pin. Read = true, write = false.
    /// </summary>
    public bool RW;

    /// <summary>
    /// Interrupt request pin. May be activated by either a transition on PA7,
    /// or timeout of the interval timer.
    /// </summary>
    public bool Irq;

    /// <summary>
    /// Data bus pins (D0-D7).
    /// </summary>
    public byte DB;

    /// <summary>
    /// Address pins (A0-A6).
    /// </summary>
    public byte A;

    /// <summary>
    /// Peripheral A port pins (PA0-PA7).
    /// </summary>
    public byte PA;

    /// <summary>
    /// Peripheral B port pins (PB0-PB7).
    /// </summary>
    public byte PB;

    /// <summary>
    /// RAM Select pin.
    /// </summary>
    public bool RS;
}
