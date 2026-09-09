using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Aemula.Emulation.Chips.Z80;

namespace Aemula.Tests.Emulation.Chips.Z80;

// Per-instruction bus-timing suite driven by the FUSE emulator's freely
// redistributable z80/tests/tests.in and tests.expected data (committed under
// Assets/; downloaded from the FUSE project's SourceForge repository at
// sourceforge.net/p/fuse-emulator/fuse/ci/master/tree/z80/tests/). No code from
// FUSE is used - only the plain-text test vectors.
//
// tests.in gives, per case: the initial register file (AF BC DE HL AF' BC' DE'
// HL' IX IY SP PC MEMPTR), then "I R IFF1 IFF2 IM halted tstates", then memory
// seed lines "<addr> <byte>... -1", then a lone "-1".
//
// tests.expected gives a timestamped bus-event list - "<t> MR <addr> <val>",
// MW, MC (internal/contention), PR, PW, PC - then the final register file, then
// "I R IFF1 IFF2 IM halted <final-t>", then the memory locations that changed.
//
// The harness clocks the chip one T-state at a time (Clk = true; Clk = false),
// services memory off the control pins, and records every completed MR / MW /
// PR / PW with the T-state count at which its machine cycle ends - which is the
// convention FUSE's <t> column uses (an M1 read is logged 4 T-states after the
// cycle starts, a 3-T memory read or write 3 T-states after). MC / PC rows are
// board-level ULA contention, not anything the CPU does, so they are parsed but
// not asserted.
//
// Only the opcodes implemented so far are exercised - the unprefixed group made
// of the 8-bit loads, the 16-bit loads, the stack ops (PUSH/POP), the register
// exchanges and JP nn. InScopeOpcodes lists them; later phases widen it as they
// add microcode (add the newly implemented opcode bytes, and drop the
// corresponding prefix guard once CB/ED/DD/FD decode exists).
public class Z80ChipBusTimingTests
{
    private static readonly string AssetsPath =
        Path.Combine("Emulation", "Chips", "Z80", "Assets");

    // Unprefixed opcode bytes with working microcode as of this phase.
    private static readonly HashSet<int> InScopeOpcodes = BuildInScopeOpcodes();

    private static HashSet<int> BuildInScopeOpcodes()
    {
        var set = new HashSet<int>
        {
            0x00,             // NOP
            0x08,             // EX AF,AF'
            0xD9,             // EXX
            0xEB,             // EX DE,HL
            0xE3,             // EX (SP),HL
            0xF9,             // LD SP,HL
            0xC3,             // JP nn
            0x01, 0x11, 0x21, 0x31, // LD dd,nn
            0x02, 0x12,       // LD (BC)/(DE),A
            0x0A, 0x1A,       // LD A,(BC)/(DE)
            0x22, 0x2A,       // LD (nn),HL / LD HL,(nn)
            0x32, 0x3A,       // LD (nn),A / LD A,(nn)
            0x36,             // LD (HL),n
            0x06, 0x0E, 0x16, 0x1E, 0x26, 0x2E, 0x3E, // LD r,n
            0xC1, 0xD1, 0xE1, 0xF1, // POP qq
            0xC5, 0xD5, 0xE5, 0xF5, // PUSH qq
        };

        // LD r,r' / LD r,(HL) / LD (HL),r / HALT.
        for (var op = 0x40; op <= 0x7F; op++)
        {
            set.Add(op);
        }

        return set;
    }

    public static IEnumerable<string> InScopeCases()
    {
        var data = FuseTestData.Load(AssetsPath);

        foreach (var name in data.Names.Order(StringComparer.Ordinal))
        {
            var baseOpcode = ParseBaseOpcode(name);
            if (baseOpcode is not null && InScopeOpcodes.Contains(baseOpcode.Value))
            {
                yield return name;
            }
        }
    }

    // FUSE names an unprefixed-opcode case by its hex byte, optionally with a
    // "_n" suffix for a variant (e.g. "02_1" checks MEMPTR after LD (BC),A).
    // Anything longer (cb.., dd.., ed.., fd..) is a prefixed opcode - out of
    // scope for now.
    private static int? ParseBaseOpcode(string name)
    {
        var stem = name;
        var underscore = stem.IndexOf('_');
        if (underscore >= 0)
        {
            stem = stem[..underscore];
        }

        return stem.Length == 2
            && int.TryParse(stem, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    [Test]
    [MethodDataSource(nameof(InScopeCases))]
    public async Task BusTiming(string caseName)
    {
        var data = FuseTestData.Load(AssetsPath);
        var input = data.Inputs[caseName];
        var expected = data.Expected[caseName];

        var ram = new byte[0x10000];
        foreach (var (address, bytes) in input.MemorySeeds)
        {
            for (var i = 0; i < bytes.Count; i++)
            {
                ram[(ushort)(address + i)] = bytes[i];
            }
        }

        var cpu = new Z80Chip();
        cpu.AF.Value = input.Registers.AF;
        cpu.BC.Value = input.Registers.BC;
        cpu.DE.Value = input.Registers.DE;
        cpu.HL.Value = input.Registers.HL;
        cpu.AFalt = input.Registers.AFalt;
        cpu.BCalt = input.Registers.BCalt;
        cpu.DEalt = input.Registers.DEalt;
        cpu.HLalt = input.Registers.HLalt;
        cpu.IX.Value = input.Registers.IX;
        cpu.IY.Value = input.Registers.IY;
        cpu.SP.Value = input.Registers.SP;
        cpu.PC.Value = input.Registers.PC;
        cpu.WZ.Value = input.Registers.Memptr;
        cpu.Flags.SetFromByte(cpu.AF.F);
        cpu.I = input.I;
        cpu.R = input.R;
        cpu.IFF1 = input.Iff1;
        cpu.IFF2 = input.Iff2;
        cpu.IM = input.Im;
        cpu.Halted = input.Halted;

        var trace = new List<FuseBusEvent>();

        var lastReadAddress = (ushort)0;
        var lastReadValue = (byte)0;
        var lastFetchAddress = (ushort)0;
        var lastFetchValue = (byte)0;
        var lastWriteAddress = (ushort)0;
        var lastWriteValue = (byte)0;

        var t = 0;

        // Run for the requested number of T-states, then let the instruction in
        // flight finish (stop only on an instruction boundary).
        while (t < input.TStates || !cpu.AtInstructionBoundary)
        {
            cpu.Clk = true;
            cpu.Clk = false;
            t++;

            if (!cpu.MReq && !cpu.Rd)
            {
                var address = cpu.Address;
                var value = ram[address];
                cpu.Data = value;

                if (!cpu.M1)
                {
                    lastFetchAddress = address;
                    lastFetchValue = value;
                }
                else
                {
                    lastReadAddress = address;
                    lastReadValue = value;
                }
            }

            if (!cpu.MReq && !cpu.Wr)
            {
                lastWriteAddress = cpu.Address;
                lastWriteValue = cpu.Data;
                ram[lastWriteAddress] = lastWriteValue;
            }

            // A machine cycle's transfer is timestamped at the T-state it ends
            // on: T4 of an M1, T3 of a 3-T read or write.
            switch ((cpu.CurrentMachineCycle, cpu.CurrentState))
            {
                case (Z80Chip.MachineCycleType.OpcodeFetch, Z80Chip.TState.T4):
                    trace.Add(new FuseBusEvent(t, "MR", lastFetchAddress, lastFetchValue));
                    break;

                case (Z80Chip.MachineCycleType.MemoryRead, Z80Chip.TState.T3):
                    trace.Add(new FuseBusEvent(t, "MR", lastReadAddress, lastReadValue));
                    break;

                case (Z80Chip.MachineCycleType.MemoryWrite, Z80Chip.TState.T3):
                    trace.Add(new FuseBusEvent(t, "MW", lastWriteAddress, lastWriteValue));
                    break;
            }

            if (t > 200)
            {
                Assert.Fail($"{caseName}: instruction did not complete within 200 T-states");
            }
        }

        // --- Bus-event trace -------------------------------------------------
        var expectedTrace = expected.Events
            .Where(e => e.Kind is "MR" or "MW" or "PR" or "PW")
            .ToList();

        await Assert.That(Format(trace)).IsEqualTo(Format(expectedTrace));

        // --- Final register file ------------------------------------------
        cpu.AF.F = cpu.Flags.AsByte();

        await Assert.That(cpu.AF.Value).IsEqualTo(expected.Registers.AF).Because("AF");
        await Assert.That(cpu.BC.Value).IsEqualTo(expected.Registers.BC).Because("BC");
        await Assert.That(cpu.DE.Value).IsEqualTo(expected.Registers.DE).Because("DE");
        await Assert.That(cpu.HL.Value).IsEqualTo(expected.Registers.HL).Because("HL");
        await Assert.That(cpu.AFalt).IsEqualTo(expected.Registers.AFalt).Because("AF'");
        await Assert.That(cpu.BCalt).IsEqualTo(expected.Registers.BCalt).Because("BC'");
        await Assert.That(cpu.DEalt).IsEqualTo(expected.Registers.DEalt).Because("DE'");
        await Assert.That(cpu.HLalt).IsEqualTo(expected.Registers.HLalt).Because("HL'");
        await Assert.That(cpu.IX.Value).IsEqualTo(expected.Registers.IX).Because("IX");
        await Assert.That(cpu.IY.Value).IsEqualTo(expected.Registers.IY).Because("IY");
        await Assert.That(cpu.SP.Value).IsEqualTo(expected.Registers.SP).Because("SP");
        await Assert.That(cpu.PC.Value).IsEqualTo(expected.Registers.PC).Because("PC");
        await Assert.That(cpu.WZ.Value).IsEqualTo(expected.Registers.Memptr).Because("MEMPTR");
        await Assert.That(cpu.I).IsEqualTo(expected.I).Because("I");
        await Assert.That(cpu.R).IsEqualTo(expected.R).Because("R");
        await Assert.That(cpu.IFF1).IsEqualTo(expected.Iff1).Because("IFF1");
        await Assert.That(cpu.IFF2).IsEqualTo(expected.Iff2).Because("IFF2");
        await Assert.That(cpu.IM).IsEqualTo(expected.Im).Because("IM");
        await Assert.That(cpu.Halted).IsEqualTo(expected.Halted).Because("halted");
        await Assert.That(t).IsEqualTo(expected.TStates).Because("total T-states");

        // --- Memory the case says should have changed -----------------------
        foreach (var (address, bytes) in expected.MemoryChanges)
        {
            for (var i = 0; i < bytes.Count; i++)
            {
                await Assert.That(ram[(ushort)(address + i)])
                    .IsEqualTo(bytes[i])
                    .Because($"memory at 0x{(ushort)(address + i):X4}");
            }
        }
    }

    private static string Format(IEnumerable<FuseBusEvent> events) =>
        string.Join("\n", events.Select(e => $"{e.Time,5} {e.Kind} {e.Address:X4} {e.Value:X2}"));
}
