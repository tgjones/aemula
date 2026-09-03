using System.IO;
using System.Runtime.CompilerServices;
using Aemula.Emulation.Systems.Nes.Mappers;

namespace Aemula.Emulation.Systems.Nes;

/// <summary>
/// The cartridge as a connector: CPU-side and PPU-side pins, wired to the
/// 2A03 / 2C02 / mainboard by <see cref="NesSystem"/> each cycle. The board's
/// bank / mirroring / WRAM decisions live in a <see cref="Mapper"/> strategy;
/// this type does only the connector-pin mechanics (including the PPU AD0-7
/// address/data multiplex, latched here on ALE just as the real board's
/// 74LS373 does).
/// </summary>
public sealed partial class Cartridge
{
    /// <summary>
    /// Loads a cartridge from a .nes file.
    /// </summary>
    public static Cartridge FromFile(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var reader = new BinaryReader(stream);
        return new Cartridge(reader);
    }

    private readonly Mapper _mapper;

    public Mapper Mapper => _mapper;

    private unsafe Cartridge(BinaryReader reader)
    {
        // Read header.
        var headerBytes = reader.ReadBytes(16);
        FileHeader header;
        fixed (void* headerBytesPtr = headerBytes)
        {
            header = Unsafe.Read<FileHeader>(headerBytesPtr);
        }

        if (header.Name[0] != 'N' || header.Name[1] != 'E' || header.Name[2] != 'S' || header.Name[3] != 0x1A)
        {
            throw new InvalidDataException();
        }

        if (header.Mapper1.ContainsTrainer)
        {
            reader.ReadBytes(512); // TODO
        }

        var prgRom = reader.ReadBytes(16384 * header.PrgRomSize);

        byte[] chr;
        bool chrIsRam;
        if (header.ChrRomSize == 0)
        {
            chr = new byte[8192];
            chrIsRam = true;
        }
        else
        {
            chr = reader.ReadBytes(8192 * header.ChrRomSize);
            chrIsRam = false;
        }

        var mapperNumber = header.Mapper1.MapperLo | (header.Mapper2.MapperHi << 4);
        var mirroring = header.Mapper1.IgnoreMirroringControl
            ? NametableMirroring.FourScreen
            : header.Mapper1.NametableMirroring;

        _mapper = Mapper.Create(
            mapperNumber,
            new MapperConfig(prgRom, chr, chrIsRam, mirroring, header.Mapper1.ContainsPrgRam));
    }

    // ---- CPU-side connector pins -------------------------------------------
    //
    // A0-A14 (no A15 on the connector), R/W̄, and the mainboard-derived
    // /ROMSEL (low ⇒ CPU is in $8000-$FFFF this cycle). M2 (φ2) is also a
    // connector pin but only gates timing, which NesSystem already handles.

    private ushort _cpuAddress;   // A0-A14 with A15 reconstructed from /ROMSEL
    private bool _cpuRw;

    internal void SetCpuBus(ushort address15, bool cpuRw, bool romSel)
    {
        // The connector carries A0-A14; /ROMSEL is the mainboard's decoded A15.
        // Reconstruct the full address once here so the mapper decodes plain
        // ranges. M2 (also a connector pin) already gates the call - NesSystem
        // only services the bus on φ2 - so it isn't threaded further.
        _cpuAddress = (ushort)((address15 & 0x7FFF) | (romSel ? 0x8000 : 0));
        _cpuRw = cpuRw;
    }

    /// <summary>
    /// The byte the cartridge is driving onto D0-D7, or <c>null</c> when it is
    /// not driving the bus (so the caller leaves the last value there - open bus).
    /// </summary>
    internal byte? CpuData => _mapper.CpuRead(_cpuAddress);

    /// <summary>A CPU write reaching the cartridge (WRAM, or a mapper register).</summary>
    internal void CpuWrite(byte data) => _mapper.CpuWrite(_cpuAddress, data);

    // ---- PPU-side connector pins -----------------------------------------------
    //
    // AD0-7 carry the low address byte during ALE and the CHR data byte during a
    // read/write; A8-A13 are separate. The cartridge latches AD0-7 on ALE (its
    // own 74LS373) and forms the 14-bit CHR address from that plus A8-A13.

    private byte _ppuAdLatch;   // latched AD0-7 (low CHR address byte)
    private byte _ppuAddressHigh; // A8-A13

    internal void SetPpuBus(byte ad, byte addressHigh, bool ale)
    {
        _ppuAddressHigh = (byte)(addressHigh & 0x3F);
        if (ale)
        {
            _ppuAdLatch = ad; // transparent while ALE asserted
        }
    }

    private ushort PpuAddress => (ushort)((_ppuAddressHigh << 8) | _ppuAdLatch);

    /// <summary>
    /// The <c>CIRAM /CE</c> pin, seen as "true ⇒ the console name-table SRAM is
    /// selected". NROM drives it from /PA13; a board with its own name-table RAM
    /// holds it off.
    /// </summary>
    internal bool CiramCe => _mapper.CiramCe(PpuAddress);

    /// <summary>The <c>CIRAM A10</c> pin for the currently latched PPU address.</summary>
    internal bool CiramA10 => _mapper.CiramA10(PpuAddress);

    /// <summary>Offset into the 2 KB console CIRAM for a raw $2000-$3FFF address.</summary>
    internal int CiramOffset(ushort ppuAddress) =>
        (_mapper.CiramA10(ppuAddress) ? 0x400 : 0) | (ppuAddress & 0x03FF);

    /// <summary>CHR read - the cartridge driving AD0-7 back with pattern data.</summary>
    internal byte PpuRead() => _mapper.ChrRead(PpuAddress);

    /// <summary>CHR write (only lands if the board has CHR RAM).</summary>
    internal void PpuWrite(byte data) => _mapper.ChrWrite(PpuAddress, data);

    // ---- Side-effect-free peeks for the debugger / test oracles ---------------

    internal byte PeekCpu(ushort address) => _mapper.PeekCpu(address);

    internal void PokeCpu(ushort address, byte value) => _mapper.PokeCpu(address, value);

    internal byte PeekPpu(ushort address) => _mapper.PeekChr(address);
}
