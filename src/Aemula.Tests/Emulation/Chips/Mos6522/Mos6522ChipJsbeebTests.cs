using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Aemula.Emulation.Chips.Mos6522;

namespace Aemula.Tests.Emulation.Chips.Mos6522;

// Replays the scenarios recorded in Assets/jsbeeb-via-timing.txt, produced by
// tools/jsbeeb-harness/via-timing.mjs from jsbeeb's own 6522, and checks that
// every register read and the IRQ line seen at the start of every cycle agree.
public class Mos6522ChipJsbeebTests
{
    private sealed record Op(int Cycle, char Kind, byte Register, byte Value);

    private sealed record Scenario(string Name, int Cycles, List<Op> Ops, Dictionary<int, bool> Irq);

    private static List<Scenario> LoadScenarios()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Emulation", "Chips", "Mos6522", "Assets", "jsbeeb-via-timing.txt");
        var scenarios = new List<Scenario>();
        Scenario? current = null;

        foreach (var line in File.ReadAllLines(path))
        {
            var parts = line.Split(' ');

            switch (parts[0])
            {
                case "scenario":
                    current = new Scenario(parts[1], int.Parse(parts[2]), [], []);
                    scenarios.Add(current);
                    break;

                case "i":
                    current!.Irq[int.Parse(parts[1])] = parts[2] == "1";
                    break;

                default:
                    current!.Ops.Add(new Op(
                        int.Parse(parts[1]),
                        parts[0][0],
                        Convert.ToByte(parts[2], 16),
                        Convert.ToByte(parts[3], 16)));
                    break;
            }
        }

        return scenarios;
    }

    [Test]
    public async Task TimerAndInterruptTimingMatchesJsbeeb()
    {
        var mismatches = new List<string>();

        foreach (var scenario in LoadScenarios())
        {
            var via = Mos6522Bus.Reset();

            for (var cycle = 0; cycle < scenario.Cycles; cycle++)
            {
                if (via.Irq != scenario.Irq[cycle])
                {
                    mismatches.Add($"{scenario.Name}: cycle {cycle} IRQ line {(via.Irq ? "high" : "low")}, jsbeeb {(scenario.Irq[cycle] ? "high" : "low")}");
                }

                Op? op = null;

                foreach (var candidate in scenario.Ops)
                {
                    if (candidate.Cycle == cycle)
                    {
                        op = candidate;
                        break;
                    }
                }

                if (op is null)
                {
                    Mos6522Bus.Idle(via);
                }
                else if (op.Kind == 'w')
                {
                    Mos6522Bus.Write(via, op.Register, op.Value);
                }
                else
                {
                    var value = Mos6522Bus.Read(via, op.Register);

                    if (value != op.Value)
                    {
                        mismatches.Add($"{scenario.Name}: cycle {cycle} read reg {op.Register:X} = {value:X2}, jsbeeb {op.Value:X2}");
                    }
                }
            }
        }

        await Assert.That(mismatches).IsEmpty();
    }
}
