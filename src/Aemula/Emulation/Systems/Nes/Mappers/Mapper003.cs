namespace Aemula.Emulation.Systems.Nes.Mappers;

/// <summary>
/// Mapper 3 - CNROM. PRG is fixed like NROM (16 KB mirrored, or 32 KB straight);
/// a write anywhere in $8000-$FFFF selects an 8 KB CHR-ROM bank. Mirroring is
/// fixed by the board. Bus conflicts are not modelled.
/// </summary>
internal sealed class Mapper003 : BankedMapper
{
    private int _chrBank;

    public Mapper003(MapperConfig config)
        : base(config, hasWram: false)
    {
    }

    protected override int PrgOffset(ushort address) => address & 0x7FFF;

    protected override int ChrOffset(ushort address) => (_chrBank * 0x2000) | (address & 0x1FFF);

    protected override void WriteRegister(ushort address, byte data) => _chrBank = data & 0x0F;
}
