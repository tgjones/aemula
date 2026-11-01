using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using Aemula.Emulation.Peripherals.Cassette;

namespace Aemula.Tests.Emulation.Peripherals;

public class UefReaderTests
{
    private static byte[] Uef(params (ushort Id, byte[] Data)[] chunks)
    {
        var bytes = new List<byte>(Encoding.ASCII.GetBytes("UEF File!\0"));
        bytes.AddRange([0x0A, 0x00]);

        foreach (var (id, data) in chunks)
        {
            bytes.AddRange(System.BitConverter.GetBytes(id));
            bytes.AddRange(System.BitConverter.GetBytes((uint)data.Length));
            bytes.AddRange(data);
        }

        return bytes.ToArray();
    }

    private static int RisingEdges(float[] samples)
    {
        var edges = 0;

        for (var i = 0; i < samples.Length; i++)
        {
            if (samples[i] > 0 && (i == 0 || samples[i - 1] <= 0))
            {
                edges++;
            }
        }

        return edges;
    }

    [Test]
    public async Task A_carrier_chunk_is_that_many_cycles_of_2400Hz()
    {
        var (samples, rate) = UefReader.Read(new MemoryStream(Uef((0x0110, [0xE8, 0x03]))));

        await Assert.That(rate).IsEqualTo(48_000);
        await Assert.That(samples.Length).IsEqualTo(1000 * 20);
        await Assert.That(RisingEdges(samples)).IsEqualTo(1000);
    }

    [Test]
    public async Task A_data_byte_is_a_start_bit_eight_data_bits_and_a_stop_bit()
    {
        // 0x01: start 0 (one 1200Hz cycle), then 1 (two 2400Hz), seven 0s,
        // stop 1: 2 + 7 one-cycle bits + 2 + 2 = 11 cycles of tone, 1 start,
        // 7 zero data bits => (1 + 7) one-cycle bits and (1 + 1) two-cycle
        // bits are 8 + 4 = 12 rising edges.
        var (samples, _) = UefReader.Read(new MemoryStream(Uef((0x0100, [0x01]))));

        await Assert.That(samples.Length).IsEqualTo(10 * 40);
        await Assert.That(RisingEdges(samples)).IsEqualTo(12);
    }

    [Test]
    public async Task Gaps_are_silence()
    {
        var floatGap = System.BitConverter.GetBytes(0.5f);
        var (samples, _) = UefReader.Read(new MemoryStream(Uef((0x0116, floatGap))));

        await Assert.That(samples.Length).IsEqualTo(24_000);
        await Assert.That(System.Array.TrueForAll(samples, sample => sample == 0f)).IsTrue();
    }

    [Test]
    public async Task A_gzipped_file_is_read_the_same()
    {
        var raw = Uef((0x0110, [0x64, 0x00]));
        using var compressed = new MemoryStream();

        using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(raw);
        }

        var (samples, _) = UefReader.Read(new MemoryStream(compressed.ToArray()));

        await Assert.That(RisingEdges(samples)).IsEqualTo(100);
    }

    [Test]
    public async Task Something_else_is_rejected()
    {
        await Assert.That(() => UefReader.Read(new MemoryStream([1, 2, 3, 4]))).Throws<InvalidDataException>();
    }
}
