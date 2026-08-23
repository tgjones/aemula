using System;
using static Aemula.BitUtility;

namespace Aemula.Emulation.Systems.Atari2600;

internal abstract class Cartridge
{
    /// <summary>
    /// Address pins A0..A10/A11 are used to address the ROM memory.
    /// Address pin A12 is used as a chip select (active high).
    /// </summary>
    public ushort Address { private get; set; }

    /// <summary>
    /// Data pins. Real cartridge ROMs have no clock pin - an EPROM's output
    /// is just "valid tPROP after the address is stable" - so this is purely
    /// combinational, no cycle call needed. Null (high-impedance) when A12
    /// isn't asserted, same shape as
    /// <see cref="Aemula.Emulation.Chips.Ttl8T97Chip"/>'s tri-state outputs.
    /// </summary>
    public byte? Data => GetBitAsBoolean(Address, 12) ? ReadRom(Address) : null;

    public static Cartridge FromData(byte[] data)
    {
        return data.Length switch
        {
            2048 => new Cartridge2K(data),
            4096 => new Cartridge4K(data),
            _ => throw new InvalidOperationException("Unknown cartridge type")
        };
    }

    protected abstract byte ReadRom(ushort address);

    public abstract byte ReadByteDebug(ushort address);
}

internal sealed class Cartridge2K : Cartridge
{
    private readonly byte[] _data;

    public Cartridge2K(byte[] data)
    {
        _data = data;
    }

    protected override byte ReadRom(ushort address) => _data[address & 0x7FF];

    public override byte ReadByteDebug(ushort address) => _data[address & 0x7FF];
}

internal sealed class Cartridge4K : Cartridge
{
    private readonly byte[] _data;

    public Cartridge4K(byte[] data)
    {
        _data = data;
    }

    protected override byte ReadRom(ushort address) => _data[address & 0xFFF];

    public override byte ReadByteDebug(ushort address) => _data[address & 0xFFF];
}
