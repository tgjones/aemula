using System.Runtime.InteropServices;

namespace Aemula.Emulation.Chips.Ricoh2C02;

public struct Ricoh2C02Pins
{
    public bool CpuRW;
    public byte CpuAddress;
    public byte CpuData;

    /// <summary>
    /// To save pins, the PPU multiplexes the lower eight VRAM address pins, also using them as the VRAM data pins.
    /// </summary>
    public PpuAddressData PpuAddressData;

    /// <summary>
    /// Address Latch Enable
    /// </summary>
    public bool PpuAle;

    /// <summary>
    /// False if PPU is reading from VRAM, otherwise true (active low).
    /// </summary>
    public bool PpuRD;

    /// <summary>
    /// False if PPU is writing to VRAM, otherwise true (active low).
    /// </summary>
    public bool PpuWR;

    /// <summary>
    /// Connected to CPU NMI pin. Active-low, edge-triggered.
    /// </summary>
    public bool Nmi;
}

[StructLayout(LayoutKind.Explicit)]
public struct PpuAddressData
{
    [FieldOffset(0)]
    public ushort Address;

    [FieldOffset(1)]
    public byte AddressHi;

    [FieldOffset(0)]
    public byte Data;
}
