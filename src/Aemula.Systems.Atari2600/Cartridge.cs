using System;
using static Aemula.Core.BitUtility;

namespace Aemula.Systems.Atari2600;

internal struct CartridgePins
{
    /// <summary>
    /// Address pins A0..A10/A11 are used to address the ROM memory.
    /// Address pin A12 is used as a chip select.
    /// </summary>
    public ushort A;

    public byte D;
}

internal abstract class Cartridge
{
    public CartridgePins Pins;

    public static Cartridge FromData(byte[] data)
    {
        return data.Length switch
        {
            2048 => new Cartridge2K(data),
            4096 => new Cartridge4K(data),
            _ => throw new InvalidOperationException("Unknown cartridge type")
        };
    }

    public abstract void Cycle();

    public abstract byte ReadByteDebug(ushort address);
}

internal sealed class Cartridge2K : Cartridge
{
    private readonly byte[] _data;

    public Cartridge2K(byte[] data)
    {
        _data = data;
    }

    public override void Cycle()
    {
        ref var pins = ref Pins;
        if (GetBitAsBoolean(pins.A, 12))
        {
            pins.D = _data[pins.A & 0x7FF];
        }
    }

    public override byte ReadByteDebug(ushort address)
    {
        return _data[address & 0x7FF];
    }
}

internal sealed class Cartridge4K : Cartridge
{
    private readonly byte[] _data;

    public Cartridge4K(byte[] data)
    {
        _data = data;
    }

    public override void Cycle()
    {
        ref var pins = ref Pins;
        if (GetBitAsBoolean(pins.A, 12))
        {
            pins.D = _data[pins.A & 0xFFF];
        }
    }

    public override byte ReadByteDebug(ushort address)
    {
        return _data[address & 0xFFF];
    }
}
