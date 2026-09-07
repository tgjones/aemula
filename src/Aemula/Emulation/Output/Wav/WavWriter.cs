using System;
using System.Buffers.Binary;
using System.IO;

namespace Aemula.Emulation.Output.Wav;

/// <summary>
/// Writes a stream of mono <see cref="float"/> samples (nominally [-1, 1]) as a
/// 16-bit PCM mono WAV file.
/// </summary>
public static class WavWriter
{
    public static void Write(string path, ReadOnlySpan<float> samples, int sampleRate)
    {
        using var stream = File.Create(path);
        Write(stream, samples, sampleRate);
    }

    public static void Write(Stream stream, ReadOnlySpan<float> samples, int sampleRate)
    {
        const ushort channels = 1;
        const ushort bitsPerSample = 16;
        const int bytesPerSample = bitsPerSample / 8;

        var dataSize = samples.Length * bytesPerSample;
        var writer = new BinaryWriter(stream);

        writer.Write("RIFF"u8);
        writer.Write((uint)(36 + dataSize));
        writer.Write("WAVE"u8);

        writer.Write("fmt "u8);
        writer.Write(16u);                                  // PCM fmt chunk size
        writer.Write((ushort)1);                            // PCM
        writer.Write(channels);
        writer.Write((uint)sampleRate);
        writer.Write((uint)(sampleRate * channels * bytesPerSample)); // byte rate
        writer.Write((ushort)(channels * bytesPerSample));  // block align
        writer.Write(bitsPerSample);

        writer.Write("data"u8);
        writer.Write((uint)dataSize);

        Span<byte> pcm = stackalloc byte[bytesPerSample];
        foreach (var sample in samples)
        {
            var clamped = Math.Clamp(sample, -1f, 1f);
            var value = (short)Math.Round(clamped * 32767f);
            BinaryPrimitives.WriteInt16LittleEndian(pcm, value);
            writer.Write(pcm);
        }

        writer.Flush();
    }
}
