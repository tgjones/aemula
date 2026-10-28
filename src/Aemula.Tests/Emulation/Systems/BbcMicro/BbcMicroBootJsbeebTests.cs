using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Aemula.Emulation.Chips.Mos6522;
using Aemula.Emulation.Systems.BbcMicro;

namespace Aemula.Tests.Emulation.Systems.BbcMicro;

// Boots OS 1.20 from power-on to its first keyboard poll (the fetch at &F02A)
// and compares with Assets/jsbeeb-boot-trace.txt, produced by
// tools/jsbeeb-harness/boot-trace.mjs from jsbeeb: the instruction sequence,
// when each instruction is fetched, and a zero-page and VIA register checkpoint at the poll.
//
// Neither the real board nor jsbeeb ever holds the CPU back for a video fetch (the RAM
// is accessed at 4MHz, alternately by the CPU and the video ULA), so the video fetches
// the OS starts late in the boot add nothing to disagree about.
//
// The first 158,651 instructions (507,445 cycles) are the same sequence as jsbeeb's. Their
// timing agrees up to the phase of the 1 MHz clock at power-on, which is 0 or 1 two-MHz
// cycles depending on the machine and shows from the first 1 MHz-bus access with no drift
// after it. Only the zero page is compared with jsbeeb's RAM, as the OS has not initialised
// the rest by then (the stack page and workspace hold arbitrary power-on contents). Of the
// VIA registers, ORA/DDRA/ORB/DDRB/ACR/PCR/IER match; the timer latches do not (undefined
// after reset).
public class BbcMicroBootJsbeebTests
{
    private const ushort ResetVector = 0xD9CD;
    private const ushort KeyboardPoll = 0xF02A;

    private sealed record Fetch(int Cycle, ushort Pc);

    private sealed record Checkpoint(string ZeroPage, Dictionary<string, string> Vias);

    private static (List<Fetch> Fetches, Dictionary<ushort, Checkpoint> Checkpoints) LoadFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Emulation", "Systems", "BbcMicro", "Assets", "jsbeeb-boot-trace.txt");
        var fetches = new List<Fetch>();
        var checkpoints = new Dictionary<ushort, Checkpoint>();
        ushort pc = 0;
        string zeroPage = "";
        Dictionary<string, string>? vias = null;

        foreach (var line in File.ReadAllLines(path))
        {
            var parts = line.Split(' ', 2);
            switch (parts[0])
            {
                case "i":
                    var fields = parts[1].Split(' ');
                    fetches.Add(new Fetch(int.Parse(fields[0]), Convert.ToUInt16(fields[1], 16)));
                    break;
                case "c":
                    pc = Convert.ToUInt16(parts[1], 16);
                    vias = [];
                    checkpoints[pc] = new Checkpoint("", vias);
                    break;
                case "ram":
                    zeroPage = parts[1];
                    break;
                case "via":
                    var name = parts[1].Split(' ', 2);
                    vias![name[0]] = name[1];
                    checkpoints[pc] = new Checkpoint(zeroPage, vias);
                    break;
            }
        }

        return (fetches, checkpoints);
    }

    private static string ViaState(Mos6522Chip via) =>
        string.Join(' ', new[]
        {
            via.PaOutput, via.PaDirection, via.OrbValue, (byte)(via.PbDirection & 0x7F),
            via.AcrValue, via.PcrValue, via.IerValue,
        }.Select(b => b.ToString("x2")));

    [Test]
    public async Task BootToTheFirstKeyboardPollMatchesJsbeeb()
    {
        var (expected, checkpoints) = LoadFixture();
        var system = new BbcMicroSystem(null, 100);

        var actual = new List<Fetch>();
        var seen = new Dictionary<ushort, Checkpoint>();
        long firstTick = -1;
        var wasHigh = system.Cpu.Phi2;

        for (var tick = 0L; tick < 8_000_000 && (actual.Count == 0 || actual[^1].Pc != KeyboardPoll); tick++)
        {
            system.Tick();

            var high = system.Cpu.Phi2;
            if (high && !wasHigh && system.Cpu.Sync)
            {
                var pc = system.Cpu.Address;
                if (firstTick < 0 && pc == ResetVector)
                {
                    firstTick = tick;
                }

                if (firstTick >= 0)
                {
                    actual.Add(new Fetch((int)((tick - firstTick) / 8), pc));

                    if (checkpoints.ContainsKey(pc) && !seen.ContainsKey(pc))
                    {
                        var zeroPage = Enumerable.Range(0, 0x100).Select(a => system.ReadByteDebug((ushort)a)).ToArray();
                        seen[pc] = new Checkpoint(
                            Convert.ToHexString(zeroPage).ToLowerInvariant(),
                            new Dictionary<string, string>
                            {
                                ["system"] = ViaState(system.SystemVia),
                                ["user"] = ViaState(system.UserVia),
                            });
                    }
                }
            }

            wasHigh = high;
        }

        // The same instructions in the same order.
        await Assert.That(actual.Count).IsEqualTo(expected.Count);
        var firstWrongPc = Enumerable.Range(0, Math.Min(actual.Count, expected.Count))
            .FirstOrDefault(i => actual[i].Pc != expected[i].Pc, -1);
        await Assert.That(firstWrongPc).IsEqualTo(-1);

        // The same timing, up to the phase of the 1MHz clock at power-on, which
        // is whatever the two machines happened to start with: it shows as a
        // fixed shift (at most one 2MHz cycle) from the first 1MHz-bus access
        // on, and must not drift after that.
        var shifts = actual.Select((f, i) => f.Cycle - expected[i].Cycle).ToList();
        await Assert.That(shifts.Min()).IsGreaterThanOrEqualTo(0);
        await Assert.That(shifts.Max()).IsLessThanOrEqualTo(1);
        await Assert.That(shifts.Skip(1).Zip(shifts, (next, previous) => next - previous).Count(d => d != 0))
            .IsLessThanOrEqualTo(1);

        foreach (var (pc, want) in checkpoints)
        {
            await Assert.That(seen.ContainsKey(pc)).IsTrue();
            var got = seen[pc];
            await Assert.That(got.ZeroPage).IsEqualTo(want.ZeroPage);
            await Assert.That(got.Vias["system"]).IsEqualTo(want.Vias["system"]);
            await Assert.That(got.Vias["user"]).IsEqualTo(want.Vias["user"]);
        }
    }
}
