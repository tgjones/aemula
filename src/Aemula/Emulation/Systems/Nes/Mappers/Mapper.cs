using System;

namespace Aemula.Emulation.Systems.Nes.Mappers;

/// <summary>
/// Everything about a cartridge board that a bare NES connector cannot decide
/// for itself. <see cref="Cartridge"/> owns the connector pins (including the
/// PPU AD0-7 address/data multiplex) and forwards demuxed bus accesses here; the
/// mapper owns all cartridge memory - PRG ROM, CHR ROM-or-RAM, any WRAM, its
/// bank registers - and exposes only behaviour. Where those bytes live, how big
/// they are and whether a region is even enabled are the concrete mapper's
/// business, never the connector's.
/// </summary>
public abstract class Mapper
{
    protected Mapper(MapperConfig config)
    {
        Mirroring = config.Mirroring;
    }

    /// <summary>Current name-table wiring. Not readonly - MMC1 and friends change
    /// it at runtime from a register write.</summary>
    public NametableMirroring Mirroring { get; protected set; }

    /// <summary>
    /// The byte the board drives onto D0-D7 for a CPU read of
    /// <paramref name="address"/> ($4020-$FFFF), or <c>null</c> when the board is
    /// not driving the bus (so the caller keeps the last value - open bus).
    /// </summary>
    public abstract byte? CpuRead(ushort address);

    /// <summary>A CPU write into cartridge space - WRAM, or a mapper register.</summary>
    public abstract void CpuWrite(ushort address, byte data);

    /// <summary>Pattern-table read ($0000-$1FFF), the board driving AD0-7 back.</summary>
    public abstract byte ChrRead(ushort address);

    /// <summary>Pattern-table write - a no-op on a CHR-ROM board.</summary>
    public abstract void ChrWrite(ushort address, byte data);

    /// <summary>
    /// The <c>CIRAM /CE</c> connector pin, seen as "true ⇒ the console name-table
    /// SRAM is selected". The common wiring is /PA13, so it tracks A13
    /// ($2000-$3FFF); a board with its own name-table RAM holds it off.
    /// </summary>
    public virtual bool CiramCe(ushort ppuAddress) =>
        (ppuAddress & 0x2000) != 0 && Mirroring != NametableMirroring.FourScreen;

    /// <summary>
    /// The <c>CIRAM A10</c> connector pin for a name-table access - the bit that
    /// selects which 1 KB CIRAM page responds.
    /// </summary>
    public bool CiramA10(ushort ppuAddress) => Mirroring switch
    {
        NametableMirroring.Horizontal => (ppuAddress & 0x0800) != 0, // follows PPU A11
        NametableMirroring.Vertical => (ppuAddress & 0x0400) != 0,   // follows PPU A10
        NametableMirroring.SingleScreenLower => false,
        NametableMirroring.SingleScreenUpper => true,
        _ => (ppuAddress & 0x0400) != 0,
    };

    /// <summary>Side-effect-free CPU read for the debugger / test oracles.</summary>
    public abstract byte PeekCpu(ushort address);

    /// <summary>Debugger poke - writes RAM only, never a mapper register.</summary>
    public abstract void PokeCpu(ushort address, byte data);

    /// <summary>Side-effect-free CHR read for the debugger / test oracles.</summary>
    public abstract byte PeekChr(ushort address);

    public static Mapper Create(int mapperNumber, MapperConfig config) => mapperNumber switch
    {
        0 => new Mapper000(config),
        2 => new Mapper002(config),
        3 => new Mapper003(config),
        1 => throw new NotSupportedException(
            "NES mapper 1 (MMC1) is not implemented yet - Phase 0 of the NES PPU plan " +
            "covers NROM/UNROM/CNROM. Use the rom_singles/ NROM builds of the test " +
            "suites instead of the combined MMC1 ROMs (see docs/nes-ppu-plan.md)."),
        _ => throw new NotSupportedException($"NES mapper {mapperNumber} is not implemented."),
    };
}

/// <summary>The parsed-header inputs <see cref="Cartridge"/> hands to
/// <see cref="Mapper.Create"/>. Goes no further than the concrete mapper's
/// constructor.</summary>
public sealed record MapperConfig(
    byte[] PrgRom,
    byte[] Chr,
    bool ChrIsRam,
    NametableMirroring Mirroring,
    bool HeaderHasPrgRam);

/// <summary>
/// Shared plumbing for the discrete-logic boards (NROM, UNROM, CNROM, ...): one
/// flat PRG image, one flat CHR image (ROM or 8 KB RAM), and an optional 8 KB
/// WRAM at $6000-$7FFF. Subclasses only say how an address maps to an offset in
/// those images and what a register write does. All storage stays private here.
/// </summary>
public abstract class BankedMapper : Mapper
{
    private readonly byte[] _prgRom;
    private readonly byte[] _chr;
    private readonly bool _chrIsRam;
    private readonly byte[]? _wram;

    protected BankedMapper(MapperConfig config, bool hasWram)
        : base(config)
    {
        _prgRom = config.PrgRom;
        _chr = config.Chr;
        _chrIsRam = config.ChrIsRam;
        _wram = hasWram ? new byte[0x2000] : null;
    }

    /// <summary>Size of the PRG image, for subclass bank arithmetic.</summary>
    protected int PrgRomLength => _prgRom.Length;

    /// <summary>Size of the CHR image, for subclass bank arithmetic.</summary>
    protected int ChrLength => _chr.Length;

    /// <summary>Maps a CPU address ($8000-$FFFF) to a PRG-image offset.</summary>
    protected abstract int PrgOffset(ushort address);

    /// <summary>Maps a PPU address ($0000-$1FFF) to a CHR-image offset. The
    /// default is a straight mapping (NROM / UNROM).</summary>
    protected virtual int ChrOffset(ushort address) => address & 0x1FFF;

    /// <summary>Handles a write into $8000-$FFFF. The base board has no registers.</summary>
    protected virtual void WriteRegister(ushort address, byte data) { }

    public sealed override byte? CpuRead(ushort address)
    {
        if (address >= 0x8000)
        {
            return _prgRom[PrgOffset(address) & (_prgRom.Length - 1)];
        }

        if (_wram is not null && address >= 0x6000)
        {
            return _wram[address & 0x1FFF];
        }

        return null;
    }

    public sealed override void CpuWrite(ushort address, byte data)
    {
        if (address >= 0x8000)
        {
            WriteRegister(address, data);
        }
        else if (_wram is not null && address >= 0x6000)
        {
            _wram[address & 0x1FFF] = data;
        }
    }

    public sealed override byte ChrRead(ushort address) =>
        _chr[ChrOffset(address) & (_chr.Length - 1)];

    public sealed override void ChrWrite(ushort address, byte data)
    {
        if (_chrIsRam)
        {
            _chr[ChrOffset(address) & (_chr.Length - 1)] = data;
        }
    }

    public sealed override byte PeekCpu(ushort address) => CpuRead(address) ?? 0;

    public sealed override void PokeCpu(ushort address, byte data)
    {
        if (_wram is not null && address is >= 0x6000 and < 0x8000)
        {
            _wram[address & 0x1FFF] = data;
        }
    }

    public sealed override byte PeekChr(ushort address) => ChrRead(address);
}
