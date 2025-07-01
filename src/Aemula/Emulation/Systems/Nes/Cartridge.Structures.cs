namespace Aemula.Emulation.Systems.Nes;

partial class Cartridge
{
    private unsafe struct FileHeader
    {
        // Bytes 0-3: "NES" followed by MS-DOS EOF.
        public fixed byte Name[4];

        // Byte 4: Size of PRG ROM in 16KB units.
        public byte PrgRomSize;

        // Byte 5: Size of CHR ROM in 8KB units (value 0 means the board uses CHR RAM).
        public byte ChrRomSize;

        // Byte 6: Mapper, mirroring, batter, trainer flags.
        public Flags6 Mapper1;

        // Byte 7: Mapper, VS/Playchoice, NES 2.0.
        public Flags7 Mapper2;

        // Byte 8: PRG-RAM size.
        public byte PrgRamSize;

        // Byte 9: TV system.
        public byte TVSystem1;

        // Byte 10: TV System, PRG-RAM presence.
        public byte TVSystem2;

        // Bytes 11-15: Unused padding.
        public fixed byte Unused[5];
    }

    private struct Flags6
    {
        public PackedByte Data;

        public NametableMirroring NametableMirroring => (NametableMirroring)Data.Get(0, 1);

        public bool ContainsPrgRam => Data.Get(1, 1) == 1;

        public bool ContainsTrainer => Data.Get(2, 1) == 1;

        public bool IgnoreMirroringControl => Data.Get(3, 1) == 1;

        public byte MapperLo => Data.Get(4, 4);
    }

    private enum NametableMirroring
    {
        Horizontal,
        Vertical,
    }

    private struct Flags7
    {
        public PackedByte Data;

        public bool IsVSUnisystem => Data.Get(0, 1) == 1;

        public bool IsPlayChoice10 => Data.Get(1, 1) == 1;

        public bool IsNes2_0Format => Data.Get(2, 2) == 2;

        public byte MapperHi => Data.Get(4, 4);
    }
}
