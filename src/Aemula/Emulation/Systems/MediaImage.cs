using System;
using System.IO;

namespace Aemula.Emulation.Systems;

/// <summary>
/// A piece of removable media as raw bytes plus the filename they came from - a
/// cartridge dump, a cassette WAV, a floppy image. Systems and peripherals take
/// one of these rather than a path, so the same insert call works whether the
/// bytes were read from a file the user picked, pulled from an embedded test
/// resource, or assembled in memory.
/// </summary>
public sealed class MediaImage
{
    private readonly byte[] _data;

    private MediaImage(string name, byte[] data)
    {
        Name = name;
        _data = data;
    }

    /// <summary>
    /// The original filename. Shown in the UI, and sniffed for an extension when
    /// a bay accepts more than one format.
    /// </summary>
    public string Name { get; }

    /// <summary>The media contents. Backed by a <see cref="byte"/> array.</summary>
    public ReadOnlySpan<byte> Data => _data;

    /// <summary>A fresh read-only stream over <see cref="Data"/>.</summary>
    public Stream OpenRead() => new MemoryStream(_data, writable: false);

    /// <summary>Reads a file from disk; <see cref="Name"/> is its filename.</summary>
    public static MediaImage FromFile(string path) =>
        new(Path.GetFileName(path), File.ReadAllBytes(path));

    /// <summary>Wraps an already-loaded buffer, tagged with a display name.</summary>
    public static MediaImage FromBytes(string name, byte[] data) => new(name, data);
}
