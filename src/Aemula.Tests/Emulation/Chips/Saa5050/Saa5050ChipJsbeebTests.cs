using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Aemula.Emulation.Chips.Saa5050;

namespace Aemula.Tests.Emulation.Chips.Saa5050;

// Replays what Assets/jsbeeb-teletext.txt records of jsbeeb's SAA5050,
// produced by tools/jsbeeb-harness/teletext.mjs: pages of character codes
// and, for each scan line of each row, the twelve half-dots and the
// foreground and background colour of every cell.
public class Saa5050ChipJsbeebTests
{
    private static readonly string[] Lines = File.ReadAllLines(Path.Combine(
        AppContext.BaseDirectory, "Emulation", "Chips", "Saa5050", "Assets", "jsbeeb-teletext.txt"));

    [Test]
    public async Task PagesMatchJsbeeb()
    {
        var mismatches = new List<string>();
        var cellsChecked = 0;

        Saa5050Chip? chip = null;
        var pageName = "";
        var codes = Array.Empty<byte>();
        var scan = 0;
        var ra0 = false;
        var tr6 = false;

        foreach (var line in Lines)
        {
            var parts = line.Split(' ');

            switch (parts[0])
            {
                case "page":
                    pageName = parts[1];
                    ra0 = parts[2] != "0";
                    chip = new Saa5050Chip();
                    var flashFields = int.Parse(parts[3], CultureInfo.InvariantCulture);

                    for (var i = 0; i < flashFields; i++)
                    {
                        chip.Dew = true;
                        chip.Dew = false;
                    }

                    chip.Dew = true;
                    chip.Dew = false;
                    chip.Crs = !ra0;
                    break;

                case "row":
                    codes = new byte[parts.Length - 1];
                    for (var i = 0; i < codes.Length; i++)
                    {
                        codes[i] = byte.Parse(parts[i + 1], NumberStyles.HexNumber);
                    }

                    scan = 0;
                    break;

                case "line":
                    // A cell is shown two character clocks after its code is
                    // taken, and LOSE follows each code a clock later; two
                    // clocks with LOSE low show the last cells and end the line.
                    for (var clock = 0; clock < codes.Length + 2; clock++)
                    {
                        chip!.Data = clock < codes.Length ? codes[clock] : (byte)' ';
                        chip.F1 = true;
                        chip.F1 = false;
                        chip.Lose = clock < codes.Length;

                        if (clock < 2)
                        {
                            continue;
                        }

                        var cell = clock - 2;
                        var expected = parts[cell + 1];
                        var dots = int.Parse(expected[..3], NumberStyles.HexNumber);
                        var foreground = int.Parse(expected[3..4], NumberStyles.HexNumber);
                        var background = int.Parse(expected[4..5], NumberStyles.HexNumber);

                        for (var dot = 0; dot < 12; dot++)
                        {
                            if (dot > 0)
                            {
                                chip.Tr6 = !tr6;
                                tr6 = !tr6;
                            }

                            var colour = ((dots >> dot) & 1) != 0 ? foreground : background;
                            var actual = (chip.Red ? 1 : 0) | (chip.Green ? 2 : 0) | (chip.Blue ? 4 : 0);

                            if (actual != colour && mismatches.Count < 8)
                            {
                                mismatches.Add($"{pageName} ra0={ra0} scan {scan} cell {cell} (code {codes[cell]:X2}) dot {dot}: expected {colour}, got {actual}");
                            }
                        }

                        cellsChecked++;
                    }

                    scan++;
                    break;
            }
        }

        await Assert.That(cellsChecked).IsGreaterThan(20000);
        await Assert.That(mismatches).IsEmpty();
    }
}
