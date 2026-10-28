using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Aemula.Emulation.Chips.VideoUla;

namespace Aemula.Tests.Emulation.Chips.VideoUla;

// Replays what Assets/jsbeeb-ula-video.txt records of jsbeeb's video ULA,
// produced by tools/jsbeeb-harness/ula-video.mjs: the logical colour of each
// 16MHz tick of a video byte in each pixel rate, and the colour a palette
// write gives with and without flash.
public class VideoUlaChipJsbeebTests
{
    private static readonly string[] Lines = File.ReadAllLines(Path.Combine(
        AppContext.BaseDirectory, "Emulation", "Chips", "VideoUla", "Assets", "jsbeeb-ula-video.txt"));

    // Palette entry i shows the colour whose bits are those of i (or of i >> 3
    // for the second pass), so the output of a pixel names its index.
    private static VideoUlaChip WithIdentityPalette(byte control, int indexShift)
    {
        var ula = new VideoUlaChip();
        ula.Write(false, control);
        for (var i = 0; i < 16; i++)
        {
            ula.Write(true, (byte)((i << 4) | (((i >> indexShift) ^ 7) & 7)));
        }

        ula.Disen = true;
        return ula;
    }

    // The colour of each tick after the one that fetched the byte.
    private static int[] Colours(VideoUlaChip ula, byte data, int ticks)
    {
        // Tick 0 of the divider fetches the byte; let the display enable through.
        ula.Data = data;
        ula.Tick();

        var colours = new int[ticks];
        for (var tick = 0; tick < ticks; tick++)
        {
            ula.Tick();
            colours[tick] = (ula.Red ? 1 : 0) | (ula.Green ? 2 : 0) | (ula.Blue ? 4 : 0);
        }

        return colours;
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PixelsMatchJsbeeb(bool fastCharacterClock)
    {
        var mismatches = new List<string>();
        var ticks = fastCharacterClock ? 8 : 16;

        foreach (var line in Lines)
        {
            var parts = line.Split(' ');
            if (parts[0] != "t")
            {
                continue;
            }

            var mode = int.Parse(parts[1], NumberStyles.HexNumber);
            var data = byte.Parse(parts[2], NumberStyles.HexNumber);
            var control = (byte)((mode << 2) | (fastCharacterClock ? 0x10 : 0));

            var low = Colours(WithIdentityPalette(control, 0), data, ticks);
            var high = Colours(WithIdentityPalette(control, 3), data, ticks);

            for (var tick = 0; tick < ticks; tick++)
            {
                var expected = int.Parse(parts[3][tick].ToString(), NumberStyles.HexNumber);
                var actual = low[tick] | ((high[tick] & 1) << 3);
                if (actual != expected && mismatches.Count < 5)
                {
                    mismatches.Add($"mode {mode} byte {data:X2} tick {tick}: expected {expected:X}, got {actual:X}");
                }
            }
        }

        await Assert.That(mismatches).IsEmpty();
    }

    [Test]
    public async Task PaletteColoursMatchJsbeebWithAndWithoutFlash()
    {
        var mismatches = new List<string>();
        var checkedCount = 0;

        foreach (var line in Lines)
        {
            var parts = line.Split(' ');
            if (parts[0] != "p")
            {
                continue;
            }

            var flash = int.Parse(parts[1], NumberStyles.HexNumber);
            var value = byte.Parse(parts[2], NumberStyles.HexNumber);
            var expected = int.Parse(parts[3], NumberStyles.HexNumber);

            // The colour depends only on the entry's four bits, so write them to
            // entry 15, which the byte &FF selects, and skip the other entries.
            if ((value >> 4) != 15)
            {
                continue;
            }

            var ula = new VideoUlaChip();
            ula.Write(false, (byte)(0x0C | flash));
            ula.Write(true, (byte)(0xF0 | (value & 0x0F)));
            ula.Disen = true;
            var actual = Colours(ula, 0xFF, 1)[0];
            checkedCount++;

            if (actual != expected && mismatches.Count < 5)
            {
                mismatches.Add($"flash {flash} value {value:X2}: expected {expected}, got {actual}");
            }
        }

        await Assert.That(checkedCount).IsEqualTo(32);
        await Assert.That(mismatches).IsEmpty();
    }
}
