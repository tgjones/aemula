using System;
using System.Buffers.Binary;
using System.IO;

namespace Aemula.Emulation.Output.Wav;

/// <summary>
/// Reads a PCM or IEEE-float WAV file down to a single stream of mono
/// <see cref="float"/> samples in the nominal range [-1, 1]. Multi-channel
/// files are averaged to mono. The sample rate is returned as-is; callers that
/// need a different rate resample themselves.
/// </summary>
public static class WavReader
{
    /// <summary>One decoded WAV: mono float samples plus their sample rate.</summary>
    public readonly record struct Result(float[] Samples, int SampleRate);

    public static Result Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    public static Result Read(Stream stream)
    {
        using var reader = new BinaryReader(stream);

        if (new string(reader.ReadChars(4)) != "RIFF")
        {
            throw new InvalidDataException("Not a RIFF file.");
        }

        reader.ReadUInt32(); // RIFF chunk size - not trusted; chunks are walked instead.

        if (new string(reader.ReadChars(4)) != "WAVE")
        {
            throw new InvalidDataException("RIFF file is not a WAVE.");
        }

        var format = (ushort)0;
        var channels = (ushort)0;
        var sampleRate = 0;
        var bitsPerSample = (ushort)0;
        byte[]? data = null;

        while (stream.Position + 8 <= stream.Length)
        {
            var chunkId = new string(reader.ReadChars(4));
            var chunkSize = reader.ReadUInt32();
            var chunkStart = stream.Position;

            switch (chunkId)
            {
                case "fmt ":
                {
                    format = reader.ReadUInt16();
                    channels = reader.ReadUInt16();
                    sampleRate = (int)reader.ReadUInt32();
                    reader.ReadUInt32(); // byte rate
                    reader.ReadUInt16(); // block align
                    bitsPerSample = reader.ReadUInt16();

                    if (format == 0xFFFE && chunkSize >= 40)
                    {
                        reader.ReadUInt16(); // cbSize
                        reader.ReadUInt16(); // valid bits per sample
                        reader.ReadUInt32(); // channel mask
                        // WAVE_FORMAT_EXTENSIBLE: the real format is the first
                        // two bytes of the SubFormat GUID.
                        format = reader.ReadUInt16();
                    }

                    break;
                }

                case "data":
                    data = reader.ReadBytes((int)chunkSize);
                    break;
            }

            // Chunks are word-aligned; skip any body we didn't consume plus a
            // pad byte for odd sizes.
            stream.Position = chunkStart + chunkSize + (chunkSize & 1);
        }

        if (data == null || channels == 0 || sampleRate == 0)
        {
            throw new InvalidDataException("WAVE file is missing a fmt or data chunk.");
        }

        var samples = Decode(data, format, bitsPerSample, channels);
        return new Result(samples, sampleRate);
    }

    private static float[] Decode(byte[] data, ushort format, ushort bitsPerSample, ushort channels)
    {
        var bytesPerSample = bitsPerSample / 8;
        if (bytesPerSample == 0)
        {
            throw new InvalidDataException($"Unsupported bits-per-sample: {bitsPerSample}.");
        }

        var frameCount = data.Length / (bytesPerSample * channels);
        var mono = new float[frameCount];

        for (var frame = 0; frame < frameCount; frame++)
        {
            var sum = 0.0;
            for (var channel = 0; channel < channels; channel++)
            {
                var offset = ((frame * channels) + channel) * bytesPerSample;
                sum += ReadOne(data, offset, format, bitsPerSample);
            }

            mono[frame] = (float)(sum / channels);
        }

        return mono;
    }

    private static double ReadOne(byte[] data, int offset, ushort format, ushort bitsPerSample)
    {
        var span = data.AsSpan(offset);

        // IEEE float (3), or EXTENSIBLE resolved to it.
        if (format == 3)
        {
            return bitsPerSample == 64
                ? BinaryPrimitives.ReadDoubleLittleEndian(span)
                : BinaryPrimitives.ReadSingleLittleEndian(span);
        }

        return bitsPerSample switch
        {
            // 8-bit PCM is unsigned, centred on 128.
            8 => (data[offset] - 128) / 128.0,
            16 => BinaryPrimitives.ReadInt16LittleEndian(span) / 32768.0,
            24 => Read24(span) / 8388608.0,
            32 => BinaryPrimitives.ReadInt32LittleEndian(span) / 2147483648.0,
            _ => throw new InvalidDataException($"Unsupported PCM bits-per-sample: {bitsPerSample}."),
        };
    }

    private static int Read24(ReadOnlySpan<byte> span)
    {
        var value = span[0] | (span[1] << 8) | (span[2] << 16);

        // Sign-extend from 24 bits.
        if ((value & 0x800000) != 0)
        {
            value |= unchecked((int)0xFF000000);
        }

        return value;
    }
}
