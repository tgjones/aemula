using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Aemula.Emulation.Chips.Motorola6845;

namespace Aemula.Tests.Emulation.Chips.Motorola6845;

// Replays the scenarios recorded in Assets/jsbeeb-crtc-timing.txt, produced by
// tools/jsbeeb-harness/crtc-timing.mjs from jsbeeb's own CRTC, and checks that
// RA, DISPEN, HS, VS, the cursor and MA agree on every character clock.
public class Motorola6845ChipJsbeebTests
{
    private sealed record Scenario(string Name, int Clocks, byte[] Registers, List<(int Clock, byte Ra, int Flags)> Events, Dictionary<int, int> Addresses);

    private static List<Scenario> LoadScenarios()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Emulation", "Chips", "Motorola6845", "Assets", "jsbeeb-crtc-timing.txt");
        var scenarios = new List<Scenario>();
        Scenario? current = null;

        foreach (var line in File.ReadAllLines(path))
        {
            var parts = line.Split(' ');

            switch (parts[0])
            {
                case "scenario":
                    var registers = Array.ConvertAll(parts[3].Split(','), r => byte.Parse(r, NumberStyles.HexNumber));
                    current = new Scenario(parts[1], int.Parse(parts[2], NumberStyles.HexNumber), registers, [], []);
                    scenarios.Add(current);
                    break;

                case "e":
                    current!.Events.Add((
                        int.Parse(parts[1], NumberStyles.HexNumber),
                        byte.Parse(parts[2], NumberStyles.HexNumber),
                        int.Parse(parts[3], NumberStyles.HexNumber)));
                    break;

                case "m":
                    current!.Addresses[int.Parse(parts[1], NumberStyles.HexNumber)] = int.Parse(parts[2], NumberStyles.HexNumber);
                    break;
            }
        }

        return scenarios;
    }

    [Test]
    public async Task OutputsMatchJsbeeb()
    {
        var mismatches = new List<string>();

        foreach (var scenario in LoadScenarios())
        {
            var crtc = Motorola6845Bus.Reset();

            for (var register = 0; register < scenario.Registers.Length; register++)
            {
                Motorola6845Bus.WriteRegister(crtc, register, scenario.Registers[register]);
            }

            // Registers are programmed first and the frame started afterwards, as
            // the start address is latched at the top of a frame.
            Motorola6845Bus.PulseReset(crtc);

            var eventIndex = 0;
            byte expectedRa = 0;
            var expectedFlags = 0;
            var reported = 0;

            for (var clock = 0; clock < scenario.Clocks && reported < 5; clock++)
            {
                while (eventIndex < scenario.Events.Count && scenario.Events[eventIndex].Clock == clock)
                {
                    expectedRa = scenario.Events[eventIndex].Ra;
                    expectedFlags = scenario.Events[eventIndex].Flags;
                    eventIndex++;
                }

                var flags = (crtc.DispEn ? 1 : 0) | (crtc.HSync ? 2 : 0) | (crtc.VSync ? 4 : 0) | (crtc.Cursor ? 8 : 0);

                if (crtc.RA != expectedRa || flags != expectedFlags)
                {
                    mismatches.Add($"{scenario.Name}: clock {clock} RA {crtc.RA:X}/flags {flags:X}, jsbeeb RA {expectedRa:X}/flags {expectedFlags:X}");
                    reported++;
                }
                else if (scenario.Addresses.TryGetValue(clock, out var expectedAddress) && crtc.MA != expectedAddress)
                {
                    mismatches.Add($"{scenario.Name}: clock {clock} MA {crtc.MA:X4}, jsbeeb {expectedAddress:X4}");
                    reported++;
                }

                Motorola6845Bus.Tick(crtc);
            }
        }

        await Assert.That(mismatches).IsEmpty();
    }
}
