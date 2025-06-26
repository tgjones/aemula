namespace Aemula.Chips.Ins8154;

public struct Ins8154Pins
{
    public byte Address;
    public byte Data;

    /// <summary>
    /// Chip Select 0. Must be low to select the chip.
    /// </summary>
    public bool CS0;

    /// <summary>
    /// Chip Select 1. Must be high to select the chip.
    /// </summary>
    public bool CS1;

    internal bool IsChipActive() => !CS0 && CS1;

    /// <summary>
    /// Read strobe.
    /// </summary>
    public bool Nrds; // TODO: Default to true

    /// <summary>
    /// Write strobe.
    /// </summary>
    public bool Nwds; // TODO: Default to true
}

public sealed class Ins8154
{
    private readonly byte[] _ram = new byte[128];

    //private void OnNrdsTransitionedHiToLo()
    //{
    //    if (!IsChipActive())
    //    {
    //        return;
    //    }

    //    _data = _ram[_address];
    //}

    //private void OnNwdsTransitionedHiToLo()
    //{
    //    if (!IsChipActive())
    //    {
    //        return;
    //    }

    //    _ram[_address] = _data;
    //}
}
