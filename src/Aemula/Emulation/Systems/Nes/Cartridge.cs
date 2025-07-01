using System.IO;
using System.Runtime.CompilerServices;
using Aemula.Emulation.Systems.Nes.Mappers;

namespace Aemula.Emulation.Systems.Nes;

public sealed partial class Cartridge
{
    /// <summary>
    /// Loads a cartridge from a .nes file.
    /// </summary>
    public static Cartridge FromFile(string filePath)
    {
        using (var stream = File.OpenRead(filePath))
        using (var reader = new BinaryReader(stream))
        {
            return new Cartridge(reader);
        }
    }

    private readonly byte[] _prgMemory;
    private readonly byte[] _chrMemory;

    // TODO: These two probably shouldn't be exposed. Access should be via the mapper.
    public byte[] PrgRom => _prgMemory;
    public byte[] ChrRom => _chrMemory;

    public readonly Mapper Mapper;

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

        _prgMemory = reader.ReadBytes(16384 * header.PrgRomSize);
        _chrMemory = reader.ReadBytes(8192 * header.ChrRomSize);

        Mapper = Mapper.Create((ushort)(header.Mapper1.MapperLo | (header.Mapper2.MapperHi << 4)));
    }
}
