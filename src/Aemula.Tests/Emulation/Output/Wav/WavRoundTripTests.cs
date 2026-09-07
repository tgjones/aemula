using System;
using System.IO;
using System.Threading.Tasks;
using Aemula.Emulation.Output.Wav;

namespace Aemula.Tests.Emulation.Output.Wav;

public class WavRoundTripTests
{
    [Test]
    public async Task SixteenBitRoundTripPreservesSamplesWithinQuantization()
    {
        var original = new float[500];
        for (var i = 0; i < original.Length; i++)
        {
            original[i] = (float)Math.Sin(i * 0.1);
        }

        using var stream = new MemoryStream();
        WavWriter.Write(stream, original, 44100);
        stream.Position = 0;
        var decoded = WavReader.Read(stream);

        await Assert.That(decoded.SampleRate).IsEqualTo(44100);
        await Assert.That(decoded.Samples.Length).IsEqualTo(original.Length);
        for (var i = 0; i < original.Length; i++)
        {
            await Assert.That(decoded.Samples[i]).IsBetween(original[i] - 0.001f, original[i] + 0.001f);
        }
    }

    [Test]
    public async Task WriterClampsOutOfRangeSamples()
    {
        float[] original = [2.0f, -2.0f, 0.5f];

        using var stream = new MemoryStream();
        WavWriter.Write(stream, original, 8000);
        stream.Position = 0;
        var decoded = WavReader.Read(stream);

        await Assert.That(decoded.Samples[0]).IsBetween(0.999f, 1.0f);
        await Assert.That(decoded.Samples[1]).IsBetween(-1.0f, -0.999f);
        await Assert.That(decoded.Samples[2]).IsBetween(0.499f, 0.501f);
    }

    [Test]
    public async Task ReadsEightBitUnsignedPcm()
    {
        // 8-bit PCM is unsigned, 128 == silence.
        byte[] data = [128, 255, 0, 192];
        var wav = BuildWav(audioFormat: 1, channels: 1, sampleRate: 8000, bitsPerSample: 8, data);

        var decoded = WavReader.Read(new MemoryStream(wav));

        await Assert.That(decoded.Samples.Length).IsEqualTo(4);
        await Assert.That(decoded.Samples[0]).IsBetween(-0.001f, 0.001f);
        await Assert.That(decoded.Samples[1]).IsBetween(0.98f, 1.0f);
        await Assert.That(decoded.Samples[2]).IsEqualTo(-1.0f);
    }

    [Test]
    public async Task AveragesStereoToMono()
    {
        // Two frames: L/R = (+1, -1) then (+0.5, +0.5).
        byte[] data =
        [
            0x00, 0x7F, 0x00, 0x81, // frame 0: +32512, -32512
            0x00, 0x40, 0x00, 0x40, // frame 1: +16384, +16384
        ];
        var wav = BuildWav(audioFormat: 1, channels: 2, sampleRate: 22050, bitsPerSample: 16, data);

        var decoded = WavReader.Read(new MemoryStream(wav));

        await Assert.That(decoded.Samples.Length).IsEqualTo(2);
        await Assert.That(decoded.Samples[0]).IsBetween(-0.01f, 0.01f);
        await Assert.That(decoded.Samples[1]).IsBetween(0.49f, 0.51f);
    }

    [Test]
    public async Task ReadsIeeeFloatPcm()
    {
        var floats = new byte[3 * 4];
        BitConverter.TryWriteBytes(floats.AsSpan(0), 0.25f);
        BitConverter.TryWriteBytes(floats.AsSpan(4), -0.75f);
        BitConverter.TryWriteBytes(floats.AsSpan(8), 1.0f);
        var wav = BuildWav(audioFormat: 3, channels: 1, sampleRate: 48000, bitsPerSample: 32, floats);

        var decoded = WavReader.Read(new MemoryStream(wav));

        await Assert.That(decoded.Samples[0]).IsEqualTo(0.25f);
        await Assert.That(decoded.Samples[1]).IsEqualTo(-0.75f);
        await Assert.That(decoded.Samples[2]).IsEqualTo(1.0f);
    }

    [Test]
    public async Task SkipsUnknownChunks()
    {
        using var inner = new MemoryStream();
        WavWriter.Write(inner, [0.1f, 0.2f, 0.3f], 16000);
        var baseline = inner.ToArray();

        // Splice a "LIST" chunk in right after the WAVE tag (offset 12).
        byte[] listChunk = [(byte)'L', (byte)'I', (byte)'S', (byte)'T', 4, 0, 0, 0, 1, 2, 3, 4];
        using var spliced = new MemoryStream();
        spliced.Write(baseline, 0, 12);
        spliced.Write(listChunk, 0, listChunk.Length);
        spliced.Write(baseline, 12, baseline.Length - 12);
        spliced.Position = 0;

        var decoded = WavReader.Read(spliced);

        await Assert.That(decoded.Samples.Length).IsEqualTo(3);
        await Assert.That(decoded.Samples[1]).IsBetween(0.199f, 0.201f);
    }

    private static byte[] BuildWav(ushort audioFormat, ushort channels, uint sampleRate, ushort bitsPerSample, byte[] data)
    {
        using var stream = new MemoryStream();
        var writer = new BinaryWriter(stream);
        var blockAlign = (ushort)(channels * (bitsPerSample / 8));

        writer.Write("RIFF"u8);
        writer.Write((uint)(36 + data.Length));
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16u);
        writer.Write(audioFormat);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * blockAlign);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write("data"u8);
        writer.Write((uint)data.Length);
        writer.Write(data);
        writer.Flush();

        return stream.ToArray();
    }
}
