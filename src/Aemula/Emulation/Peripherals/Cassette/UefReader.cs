using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Aemula.Emulation.Peripherals.Cassette;

/// <summary>
/// Reads a UEF (Universal Emulator Format) tape image, as used for BBC Micro
/// and Acorn Electron tapes, and plays it out as the audio a cassette recorder
/// would have: 1200Hz and 2400Hz square waves in the Acorn tape format (a 0 bit
/// is one cycle of the base frequency, a 1 bit is two cycles of twice it).
/// Gaps are silence. The file is gzip-compressed most of the time, and either
/// form is accepted.
/// </summary>
public static class UefReader
{
    public const int SampleRate = 48_000;

    private const float Level = 0.8f;

    public static (float[] Samples, int SampleRate) Read(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();

        if (bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
        {
            using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var inflated = new MemoryStream();
            gzip.CopyTo(inflated);
            bytes = inflated.ToArray();
        }

        const string Magic = "UEF File!\0";

        if (bytes.Length < Magic.Length + 2 || Encoding.ASCII.GetString(bytes, 0, Magic.Length) != Magic)
        {
            throw new InvalidDataException("Not a UEF file.");
        }

        var synth = new Synthesiser();
        var position = Magic.Length + 2;

        while (position + 6 <= bytes.Length)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position));
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 2));
            position += 6;

            if (length < 0 || position + length > bytes.Length)
            {
                break;
            }

            synth.Chunk(id, bytes.AsSpan(position, length));
            position += length;
        }

        return (synth.Finish(), SampleRate);
    }

    private sealed class Synthesiser
    {
        private readonly List<float> _samples = [];
        private double _end;
        private double _baseFrequency = 1200;
        private double _baud = 1200;

        public float[] Finish() => _samples.ToArray();

        public void Chunk(ushort id, ReadOnlySpan<byte> data)
        {
            switch (id)
            {
                case 0x0100:
                    foreach (var value in data)
                    {
                        Byte(value, 8, 'N', 1);
                    }

                    break;

                case 0x0104 when data.Length >= 3:
                {
                    var dataBits = data[0];
                    var parity = (char)data[1];
                    var stopBits = (sbyte)data[2];

                    foreach (var value in data[3..])
                    {
                        Byte(value, dataBits, parity, stopBits);
                    }

                    break;
                }

                case 0x0110 when data.Length >= 2:
                    Carrier(BinaryPrimitives.ReadUInt16LittleEndian(data));
                    break;

                case 0x0111 when data.Length >= 4:
                    Carrier(BinaryPrimitives.ReadUInt16LittleEndian(data));
                    Byte(0xAA, 8, 'N', 1);
                    Carrier(BinaryPrimitives.ReadUInt16LittleEndian(data[2..]));
                    break;

                case 0x0112 when data.Length >= 2:
                    Silence(BinaryPrimitives.ReadUInt16LittleEndian(data) / (2 * _baseFrequency));
                    break;

                case 0x0113 when data.Length >= 4:
                    _baseFrequency = BinaryPrimitives.ReadSingleLittleEndian(data);
                    break;

                case 0x0116 when data.Length >= 4:
                    Silence(BinaryPrimitives.ReadSingleLittleEndian(data));
                    break;

                case 0x0117 when data.Length >= 2:
                    _baud = BinaryPrimitives.ReadUInt16LittleEndian(data);
                    break;
            }
        }

        // A carrier is counted in cycles of the tone at twice the base
        // frequency.
        private void Carrier(int cycles) => Cycles(2 * _baseFrequency, cycles);

        private void Byte(byte value, int dataBits, char parity, int stopBits)
        {
            Bit(false);

            var ones = 0;

            for (var bit = 0; bit < dataBits; bit++)
            {
                var set = (value & (1 << bit)) != 0;
                ones += set ? 1 : 0;
                Bit(set);
            }

            if (parity is 'E' or 'O')
            {
                Bit(parity == 'E' ? (ones & 1) != 0 : (ones & 1) == 0);
            }

            // A negative stop-bit count means one stop bit that is followed
            // by an extra short wave.
            Bit(true);

            if (stopBits is 2 or < -1)
            {
                Bit(true);
            }

            if (stopBits < 0)
            {
                Cycles(2 * _baseFrequency, 1);
            }
        }

        private void Bit(bool one)
        {
            if (one)
            {
                Cycles(2 * _baseFrequency, (int)Math.Round(2 * _baseFrequency / _baud));
            }
            else
            {
                Cycles(_baseFrequency, (int)Math.Round(_baseFrequency / _baud));
            }
        }

        private void Cycles(double frequency, int count)
        {
            var half = SampleRate / frequency / 2;

            for (var cycle = 0; cycle < count; cycle++)
            {
                Fill(Level, half);
                Fill(-Level, half);
            }
        }

        private void Silence(double seconds) => Fill(0f, seconds * SampleRate);

        // Timing is kept as an exact running position, so a tone whose
        // half-cycle is not a whole number of samples does not drift.
        private void Fill(float level, double duration)
        {
            _end += duration;
            var target = (int)Math.Round(_end);

            while (_samples.Count < target)
            {
                _samples.Add(level);
            }
        }
    }
}
