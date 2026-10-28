using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Aemula.Emulation.Systems.BbcMicro;

namespace Aemula.Tests.Emulation.Systems.BbcMicro;

// Checks the RAM address the board forms from the 6845's address and row
// against the ones jsbeeb reads (Assets/jsbeeb-ula-video.txt, produced by
// tools/jsbeeb-harness/ula-video.mjs), for every hardware scroll size.
public class BbcMicroVideoAddressTests
{
    [Test]
    public async Task AddressesMatchJsbeeb()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Emulation", "Chips", "VideoUla", "Assets", "jsbeeb-ula-video.txt");
        var system = new BbcMicroSystem();
        var mismatches = new List<string>();
        var checkedCount = 0;

        foreach (var line in File.ReadAllLines(path))
        {
            var parts = line.Split(' ');
            if (parts[0] != "a")
            {
                continue;
            }

            var ma = ushort.Parse(parts[1], NumberStyles.HexNumber);
            var ra = byte.Parse(parts[2], NumberStyles.HexNumber);
            var scroll = int.Parse(parts[3], NumberStyles.HexNumber);
            var expected = int.Parse(parts[4], NumberStyles.HexNumber);

            // IC32 outputs 4 and 5 (C0 and C1) are the scroll bits.
            LatchOutput(system, 4, (scroll & 1) != 0);
            LatchOutput(system, 5, (scroll & 2) != 0);

            var actual = system.VideoAddressFor(ma, ra);
            checkedCount++;
            if (actual != expected && mismatches.Count < 5)
            {
                mismatches.Add($"MA {ma:X4} RA {ra} scroll {scroll}: expected {expected:X4}, got {actual:X4}");
            }
        }

        await Assert.That(checkedCount).IsGreaterThan(3000);
        await Assert.That(mismatches).IsEmpty();
    }

    private static void LatchOutput(BbcMicroSystem system, int output, bool value)
    {
        system._ic32.G = true;
        system._ic32.A0 = (output & 1) != 0;
        system._ic32.A1 = (output & 2) != 0;
        system._ic32.A2 = (output & 4) != 0;
        system._ic32.D = value;
        system._ic32.G = false;
        system._ic32.G = true;
    }
}
