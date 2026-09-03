namespace Aemula.Emulation.Systems.Nes.Mappers;

/// <summary>
/// Mapper 2 - UNROM / UOROM. $8000-$BFFF is a switchable 16 KB PRG bank selected
/// by any write into $8000-$FFFF; $C000-$FFFF is fixed to the last bank. CHR is
/// always 8 KB RAM. Mirroring is fixed by the board. Bus conflicts are not
/// modelled.
/// </summary>
internal sealed class Mapper002 : BankedMapper
{
    private int _prgBank;

    public Mapper002(MapperConfig config)
        : base(config, hasWram: false)
    {
    }

    protected override int PrgOffset(ushort address)
    {
        var bank = address < 0xC000 ? _prgBank : (PrgRomLength / 0x4000) - 1;
        return (bank * 0x4000) | (address & 0x3FFF);
    }

    protected override void WriteRegister(ushort address, byte data) => _prgBank = data & 0x0F;
}
