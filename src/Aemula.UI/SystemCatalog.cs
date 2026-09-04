using System;
using System.Collections.Generic;
using System.Linq;
using Aemula.Emulation.Systems;

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

// The id/display-name/factory triple for each entry comes from
// Aemula.Emulation.Systems.EmulatedSystems, the cross-project list of which
// systems are wired up end-to-end; this layers on the ROM-picker metadata
// that's specific to the UI's File > Open ROM / System submenu, keyed by the
// same Id. An ordered list (it drives the File > System submenu) rather than
// a dictionary, so submenu order matches EmulatedSystems.All.
public static class SystemCatalog
{
    private static readonly Dictionary<string, (RomRequirement Rom, string RomDialogTitle, RomFileFilter[] RomFilters)> RomInfoById =
        new()
        {
            ["appleii"] = (
                RomRequirement.Optional,
                "Select an Apple II ROM image",
                [new RomFileFilter("ROM images", "rom;bin"), new RomFileFilter("All files", "*")]),

            // The Monitor ROM is a fixed inlined literal (Roms/WozMonitor.cs)
            // and there's no cassette support yet (docs/apple-i-plan.md,
            // Phase 5 stretch) - nothing for File > Open ROM to pick.
            ["applei"] = (
                RomRequirement.None,
                "",
                []),

            ["atari2600"] = (
                RomRequirement.Required,
                "Select a cartridge",
                [new RomFileFilter("Atari 2600 cartridges", "a26;bin"), new RomFileFilter("All files", "*")]),

            ["nes"] = (
                RomRequirement.Required,
                "Select a cartridge",
                [new RomFileFilter("iNES cartridges", "nes"), new RomFileFilter("All files", "*")]),

            ["spaceinvaders"] = (
                RomRequirement.None,
                "",
                []),
        };

    public static readonly IReadOnlyList<SystemCatalogEntry> Entries = EmulatedSystems.All
        .Select(system =>
        {
            var (rom, romDialogTitle, romFilters) = RomInfoById[system.Id];
            return new SystemCatalogEntry(system.Id, system.DisplayName, system.Create, rom, romDialogTitle, romFilters);
        })
        .ToList();

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
