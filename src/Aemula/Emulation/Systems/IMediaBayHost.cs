using System;
using System.Collections.Generic;

namespace Aemula.Emulation.Systems;

/// <summary>
/// Something that owns one or more <see cref="MediaBay"/>s and can have media
/// inserted into or ejected from them by bay id. Implemented by
/// <see cref="EmulatedSystem"/> (a cartridge slot), by
/// <c>IPeripheral</c> (a cassette deck's tape bay), and by <see cref="Rig"/>,
/// which concatenates the system's bays with every peripheral's and forwards
/// each call to whichever host owns that bay id. Callers of the rig never learn
/// whether a bay is intrinsic to the machine or contributed by a peripheral.
/// </summary>
public interface IMediaBayHost
{
    IReadOnlyList<MediaBay> MediaBays => [];

    void InsertMedia(string bayId, MediaImage image) =>
        throw new ArgumentException($"No media bay '{bayId}'.");

    void EjectMedia(string bayId) =>
        throw new ArgumentException($"No media bay '{bayId}'.");
}
