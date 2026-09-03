namespace Aemula.Emulation.Systems.Nes.Mappers;

/// <summary>
/// Mapper 0 - NROM. No banking: 16 KB PRG is mirrored across $8000-$FFFF (the
/// <see cref="BankedMapper"/> size mask does the mirror), 32 KB PRG fills it
/// straight; CHR is a fixed 8 KB (ROM, or RAM when the header CHR size is 0).
///
/// <para>WRAM: the NROM builds of the community test suites write their result
/// protocol to $6000-$7FFF even though the iNES PRG-RAM bit is usually clear,
/// and every mainstream emulator gives NROM 8 KB there regardless - so this
/// mapper does too. That is this mapper's call.</para>
/// </summary>
internal sealed class Mapper000 : BankedMapper
{
    public Mapper000(MapperConfig config)
        : base(config, hasWram: true)
    {
    }

    protected override int PrgOffset(ushort address) => address & 0x7FFF;
}
