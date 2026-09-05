using System;
using System.IO;

namespace Aemula.Emulation.Chips;

/// <summary>
/// Signetics 2513 "64x8x5 Character Generator": a 2560-bit mask ROM, one
/// character generator, socketed unmodified on both the Apple I (ICD2) and
/// the early Apple II/II+ (Apple PN 341-0036) - see
/// Emulation/Chips/Roms/README.txt.
///
/// Pin names and character-format table from the datasheet: Address1-
/// Address3 select which of the 8 rows of the current character to read
/// out, Address4-Address9 select which of the 64 characters (6 bits);
/// Out1-Out5 are that row's 5 dot outputs, tri-stated (high-Z) unless
/// ChipEnable is asserted (active low, for output bussing - tied low on
/// both boards, since nothing else shares their video-dot bus).
/// </summary>
public sealed class Signetics2513Chip
{
    private readonly byte[] _rom;

    private Signetics2513Chip(byte[] rom)
    {
        _rom = rom;
    }

    /// <summary>
    /// Loads the one ROM image both systems share (see
    /// Emulation/Chips/Roms/README.txt) - callers just wire up pins, same
    /// as any other chip, without each needing their own copy of the
    /// load-from-disk plumbing.
    /// </summary>
    public static Signetics2513Chip Load()
    {
        var rom = new byte[512];

        var path = Path.Combine(AppContext.BaseDirectory, "Emulation", "Chips", "Roms", "Signetics2513.rom");
        using (var stream = File.OpenRead(path))
        {
            stream.ReadExactly(rom);
        }

        return new Signetics2513Chip(rom);
    }

    public bool Address1 { private get; set; }
    public bool Address2 { private get; set; }
    public bool Address3 { private get; set; }

    public bool Address4 { private get; set; }
    public bool Address5 { private get; set; }
    public bool Address6 { private get; set; }
    public bool Address7 { private get; set; }
    public bool Address8 { private get; set; }
    public bool Address9 { private get; set; }

    public bool ChipEnable { private get; set; }

    private int Row =>
        (Address3 ? 1 << 2 : 0) |
        (Address2 ? 1 << 1 : 0) |
        (Address1 ? 1 << 0 : 0);

    private int Character =>
        (Address9 ? 1 << 5 : 0) |
        (Address8 ? 1 << 4 : 0) |
        (Address7 ? 1 << 3 : 0) |
        (Address6 ? 1 << 2 : 0) |
        (Address5 ? 1 << 1 : 0) |
        (Address4 ? 1 << 0 : 0);

    // The dump's byte-per-row format leaves bit 0 and bit 6 always clear
    // (the character cell's blank spacer columns either side of the 5 real
    // dots) - Out1-Out5 read the 5 meaningful bits out of the middle.
    private byte Data => _rom[Character * 8 + Row];

    public bool? Out1 => ChipEnable ? null : (Data & 0x02) != 0;
    public bool? Out2 => ChipEnable ? null : (Data & 0x04) != 0;
    public bool? Out3 => ChipEnable ? null : (Data & 0x08) != 0;
    public bool? Out4 => ChipEnable ? null : (Data & 0x10) != 0;
    public bool? Out5 => ChipEnable ? null : (Data & 0x20) != 0;
}
