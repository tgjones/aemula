using System;
using System.Collections.Generic;
using Aemula.Emulation.Systems.AppleII;
using Aemula.Emulation.Systems.Atari2600;
using Aemula.Emulation.Systems.Nes;
using Aemula.Emulation.Systems.SpaceInvaders;

namespace Aemula.UI;

// How a system relates to a picked file, per its LoadProgram behaviour.
public enum RomRequirement
{
    // The picked path is ignored entirely - LoadProgram loads fixed ROMs
    // from the build output (Space Invaders). File > Open ROM is disabled.
    None,

    // A file is optional: the system boots without one and a file just
    // overrides part of its address space (Apple II's $D000-$FFFF). The
    // System submenu switches to it immediately, with no file.
    Optional,

    // A file is mandatory: LoadProgram can't produce a running machine
    // without a cartridge (Atari 2600 does File.ReadAllBytes on the path,
    // NES does Cartridge.FromFile). The emulation window opens the file
    // dialog *first* and only swaps systems on a successful pick.
    Required,
}

// A managed name/glob pair for the open-file dialog. Kept as plain strings
// rather than SDL's own SDLDialogFileFilter (a pair of raw byte* into native
// memory) so the catalog stays a simple static table; the pointers are
// marshalled only for the duration of a ShowOpenFileDialog call.
public readonly record struct RomFileFilter(string Name, string Pattern);

public sealed record SystemCatalogEntry(
    string Id,
    string DisplayName,
    Func<EmulatedSystem> Create,
    RomRequirement Rom,
    string RomDialogTitle,
    RomFileFilter[] RomFilters);

// Replaces the bare Program.Systems dictionary: an ordered list (it drives
// the File > System submenu) that also carries what each system needs from a
// file and how to ask for one.
public static class SystemCatalog
{
    public static readonly IReadOnlyList<SystemCatalogEntry> Entries =
    [
        new SystemCatalogEntry(
            "appleii",
            "Apple II+",
            static () => new AppleIISystem(),
            RomRequirement.Optional,
            "Select an Apple II ROM image",
            [new RomFileFilter("ROM images", "rom;bin"), new RomFileFilter("All files", "*")]),

        new SystemCatalogEntry(
            "atari2600",
            "Atari 2600",
            static () => new Atari2600System(),
            RomRequirement.Required,
            "Select a cartridge",
            [new RomFileFilter("Atari 2600 cartridges", "a26;bin"), new RomFileFilter("All files", "*")]),

        new SystemCatalogEntry(
            "nes",
            "NES",
            static () => new NesSystem(),
            RomRequirement.Required,
            "Select a cartridge",
            [new RomFileFilter("iNES cartridges", "nes"), new RomFileFilter("All files", "*")]),

        new SystemCatalogEntry(
            "spaceinvaders",
            "Space Invaders",
            static () => new SpaceInvadersSystem(),
            RomRequirement.None,
            "",
            []),
    ];

    // Boots to BASIC with no file needed - see Program's startup path.
    public static SystemCatalogEntry Default => Entries[0];

    public static SystemCatalogEntry? FindById(string? id)
    {
        if (id == null)
        {
            return null;
        }

        foreach (var entry in Entries)
        {
            if (entry.Id == id)
            {
                return entry;
            }
        }

        return null;
    }
}
