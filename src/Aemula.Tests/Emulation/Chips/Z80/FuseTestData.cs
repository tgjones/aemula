using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Aemula.Tests.Emulation.Chips.Z80;

// Parser for the FUSE emulator's z80/tests/tests.in and tests.expected vectors.
// See Z80ChipBusTimingTests for the file-format summary and provenance.

internal readonly record struct FuseRegisters(
    ushort AF,
    ushort BC,
    ushort DE,
    ushort HL,
    ushort AFalt,
    ushort BCalt,
    ushort DEalt,
    ushort HLalt,
    ushort IX,
    ushort IY,
    ushort SP,
    ushort PC,
    ushort Memptr);

internal readonly record struct FuseBusEvent(int Time, string Kind, ushort Address, byte Value);

internal sealed class FuseInput
{
    public required FuseRegisters Registers { get; init; }
    public byte I { get; init; }
    public byte R { get; init; }
    public bool Iff1 { get; init; }
    public bool Iff2 { get; init; }
    public byte Im { get; init; }
    public bool Halted { get; init; }
    public int TStates { get; init; }
    public required IReadOnlyList<(ushort Address, IReadOnlyList<byte> Bytes)> MemorySeeds { get; init; }
}

internal sealed class FuseExpected
{
    public required IReadOnlyList<FuseBusEvent> Events { get; init; }
    public required FuseRegisters Registers { get; init; }
    public byte I { get; init; }
    public byte R { get; init; }
    public bool Iff1 { get; init; }
    public bool Iff2 { get; init; }
    public byte Im { get; init; }
    public bool Halted { get; init; }
    public int TStates { get; init; }
    public required IReadOnlyList<(ushort Address, IReadOnlyList<byte> Bytes)> MemoryChanges { get; init; }
}

internal sealed class FuseTestData
{
    private static FuseTestData? _cached;
    private static string? _cachedPath;

    public required IReadOnlyDictionary<string, FuseInput> Inputs { get; init; }
    public required IReadOnlyDictionary<string, FuseExpected> Expected { get; init; }

    public IEnumerable<string> Names => Inputs.Keys.Where(Expected.ContainsKey);

    public static FuseTestData Load(string assetsPath)
    {
        if (_cached is not null && _cachedPath == assetsPath)
        {
            return _cached;
        }

        var inputs = ParseInputs(File.ReadAllLines(Path.Combine(assetsPath, "tests.in")));
        var expected = ParseExpected(File.ReadAllLines(Path.Combine(assetsPath, "tests.expected")));

        _cached = new FuseTestData { Inputs = inputs, Expected = expected };
        _cachedPath = assetsPath;
        return _cached;
    }

    private static Dictionary<string, FuseInput> ParseInputs(string[] lines)
    {
        var result = new Dictionary<string, FuseInput>(StringComparer.Ordinal);
        var i = 0;

        while (i < lines.Length)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                i++;
                continue;
            }

            var name = lines[i++].Trim();
            var registers = ParseRegisters(lines[i++]);

            var state = Tokens(lines[i++]);
            var input = new FuseInput
            {
                Registers = registers,
                I = ParseHexByte(state[0]),
                R = ParseHexByte(state[1]),
                Iff1 = state[2] == "1",
                Iff2 = state[3] == "1",
                Im = byte.Parse(state[4], CultureInfo.InvariantCulture),
                Halted = state[5] == "1",
                TStates = int.Parse(state[6], CultureInfo.InvariantCulture),
                MemorySeeds = ParseMemoryBlock(lines, ref i),
            };

            result[name] = input;
        }

        return result;
    }

    private static Dictionary<string, FuseExpected> ParseExpected(string[] lines)
    {
        var result = new Dictionary<string, FuseExpected>(StringComparer.Ordinal);
        var i = 0;

        while (i < lines.Length)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                i++;
                continue;
            }

            var name = lines[i++].Trim();

            var events = new List<FuseBusEvent>();
            while (i < lines.Length && IsEventLine(lines[i]))
            {
                var parts = Tokens(lines[i++]);
                var time = int.Parse(parts[0], CultureInfo.InvariantCulture);
                var kind = parts[1];
                ushort address = parts.Length > 2 ? ParseHexUShort(parts[2]) : (ushort)0;
                byte value = parts.Length > 3 ? ParseHexByte(parts[3]) : (byte)0;
                events.Add(new FuseBusEvent(time, kind, address, value));
            }

            var registers = ParseRegisters(lines[i++]);
            var state = Tokens(lines[i++]);

            // Some expected blocks carry no memory-change lines and no trailing
            // "-1"; ParseMemoryBlock copes with either.
            var memoryChanges = ParseMemoryBlock(lines, ref i);

            result[name] = new FuseExpected
            {
                Events = events,
                Registers = registers,
                I = ParseHexByte(state[0]),
                R = ParseHexByte(state[1]),
                Iff1 = state[2] == "1",
                Iff2 = state[3] == "1",
                Im = byte.Parse(state[4], CultureInfo.InvariantCulture),
                Halted = state[5] == "1",
                TStates = int.Parse(state[6], CultureInfo.InvariantCulture),
                MemoryChanges = memoryChanges,
            };
        }

        return result;
    }

    // A memory block is zero or more "<addr> <byte>... -1" lines, optionally
    // followed by a lone "-1". Stops at a blank line, the end of input, or a
    // line that is not a memory line.
    private static List<(ushort Address, IReadOnlyList<byte> Bytes)> ParseMemoryBlock(
        string[] lines,
        ref int i)
    {
        var block = new List<(ushort, IReadOnlyList<byte>)>();

        while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
        {
            var parts = Tokens(lines[i]);

            if (parts.Length == 1 && parts[0] == "-1")
            {
                i++;
                break;
            }

            if (parts.Length < 2 || parts[^1] != "-1")
            {
                break;
            }

            var address = ParseHexUShort(parts[0]);
            var bytes = new List<byte>();
            for (var p = 1; p < parts.Length - 1; p++)
            {
                bytes.Add(ParseHexByte(parts[p]));
            }

            block.Add((address, bytes));
            i++;
        }

        return block;
    }

    private static bool IsEventLine(string line)
    {
        var parts = Tokens(line);
        return parts.Length >= 2
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            && parts[1] is "MR" or "MW" or "MC" or "PR" or "PW" or "PC";
    }

    private static FuseRegisters ParseRegisters(string line)
    {
        var w = Tokens(line).Select(ParseHexUShort).ToArray();
        return new FuseRegisters(
            w[0], w[1], w[2], w[3], w[4], w[5], w[6], w[7], w[8], w[9], w[10], w[11], w[12]);
    }

    private static string[] Tokens(string line) =>
        line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static ushort ParseHexUShort(string token) =>
        ushort.Parse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static byte ParseHexByte(string token) =>
        byte.Parse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
