using System.Collections.Generic;

namespace Aemula.Emulation.Systems;

/// <summary>
/// One receptacle a machine (or a peripheral cabled to it) takes removable media
/// into: a cartridge slot, a cassette recorder, a disk drive. This is plain
/// capability data - the UI builds its "Media" menu and the command line builds
/// its <c>--media</c> / <c>--list-media</c> options straight off a list of these,
/// with no per-system code. The hardware word ("Cartridge slot", "Cassette
/// recorder") lives only in <see cref="DisplayName"/>; the type stays generic.
/// </summary>
/// <param name="Id">Stable, lower-case identifier - "cartridge", "cassette", "drive1".</param>
/// <param name="DisplayName">Human-readable name for menus and listings.</param>
/// <param name="Required">
/// Advisory only. A cartridge console with an empty slot still runs (off into
/// open bus); the UI uses this to open the file dialog proactively rather than
/// show a black screen, and the command line to emit a friendly error rather
/// than a garbage screenshot.
/// </param>
/// <param name="Filters">Name/glob pairs for the open-file dialog.</param>
/// <param name="DialogTitle">Title for the open-file dialog.</param>
public sealed record MediaBay(
    string Id,
    string DisplayName,
    bool Required,
    IReadOnlyList<MediaFileFilter> Filters,
    string DialogTitle);

/// <summary>
/// A name/glob pair for the open-file dialog, e.g. <c>("Atari 2600 cartridges",
/// "a26;bin")</c>. Kept as plain strings; the marshalling to SDL's native filter
/// structs happens only for the duration of a dialog call.
/// </summary>
public readonly record struct MediaFileFilter(string Name, string Pattern);
