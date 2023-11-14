using System;
using System.IO;

namespace Aemula.Systems.Nes.Tests.Visual2C02;

partial class Configuration
{
    public sealed record SegmentDefinition(ushort Node, bool Pullup, byte Unknown, int Area);

    public static readonly SegmentDefinition[] SegmentDefinitions;

    private static SegmentDefinition[] CreateSegmentDefinitions()
    {
        var lines = File.ReadAllLines("Visual2C02/Configuration.SegmentDefinitions.txt");

        var result = new SegmentDefinition[lines.Length];

        const int maxCoordinates = 1100;
        var coordinates = new ushort[maxCoordinates];

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Split(',');

            var numCoordinates = line.Length - 3;
            if (numCoordinates > maxCoordinates)
            {
                throw new InvalidOperationException($"Node {line[0]}, numCoordinates {numCoordinates} is more than maxCoordinates {maxCoordinates}");
            }

            for (var j = 0; j < numCoordinates; j++)
            {
                coordinates[j] = ushort.Parse(line[j + 3]);
            }

            var area = coordinates[numCoordinates - 2] * coordinates[1] - coordinates[0] * coordinates[numCoordinates - 1];
            for (var j = 0; j < numCoordinates - 2; j += 2)
            {
                area += coordinates[j] * coordinates[j + 3] - coordinates[j + 2] * coordinates[j + 1];
            }

            if (area < 0)
            {
                area = -area;
            }

            result[i] = new SegmentDefinition(
                ushort.Parse(line[0]),
                line[1] == "+",
                byte.Parse(line[2]),
                area);
        }

        return result;
    }
}
