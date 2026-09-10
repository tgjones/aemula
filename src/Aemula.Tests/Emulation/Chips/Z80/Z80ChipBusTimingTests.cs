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
// PR / PW as (time, kind, address, value). The recorded trace is then compared
// to the expected one as a whole - so for every bus event the assertion pins
// the address, the byte, AND the FUSE <time> column, not merely the order the
// events occur in.
//
// FUSE's <time> is a whole T-state index: tests.expected carries no finer
// sub-T "half-cycle" number, so there is nothing at that resolution to check
// against and a reader should not go looking for it. The harness counts one
// T-state per CLK pair and timestamps each transfer where FUSE logs it - on
// the falling edge of its machine cycle's last T-state: an M1 opcode read at
// T4 (4 T-states after the cycle began), a 3-T memory read or write at T3, and
// a port access one T-state into its I/O machine cycle.
//
// The MC (memory contention) and PC (port contention) rows are board-level ZX
// Spectrum ULA behaviour - the ULA freezes the CPU clock while it is drawing
// and the CPU touches contended RAM or I/O - not anything a bare Z80 does.
// Z80Chip
// neither produces nor models contention, so those rows are parsed (to keep
// the file format handling honest) but never asserted; modelling them belongs
// to a future SpectrumSystem.
//
// Every opcode group is decoded: the whole unprefixed table, the CB page
// (rotate/shift and BIT/RES/SET), the ED page (16-bit loads, ADC/SBC HL, NEG,
// IM, LD A,I/R, RRD/RLD, RETN/RETI, the IN/OUT and block instructions) and the
// DD / FD index-register page including the DD CB / FD CB double prefix and the
// inert DD FD chain - so every FUSE case is in scope and the whole suite runs.
public class Z80ChipBusTimingTests
{
    private static readonly string AssetsPath =
        Path.Combine("Emulation", "Chips", "Z80", "Assets");

    public static IEnumerable<string> InScopeCases()
    {
        var data = FuseTestData.Load(AssetsPath);

        foreach (var name in data.Names.Order(StringComparer.Ordinal))
        {
            if (IsInScope(name))
            {
                yield return name;
            }
        }
    }

    // FUSE names an unprefixed-opcode case by its hex byte, a single-escape one
    // (CB / ED / DD / FD) by the escape letters plus the opcode byte, and a
    // double-escape one (DD CB / FD CB, and the inert DD FD chain) by both
    // escape pairs plus the opcode byte - any of them optionally carrying a
    // "_n" suffix for a variant (e.g. "02_1" checks MEMPTR after LD (BC),A,
    // "edb0_2" the final pass of LDIR). Every page is decoded, so every shape
    // is in scope.
    private static bool IsInScope(string name)
    {
        var stem = name;
        var underscore = stem.IndexOf('_');
        if (underscore >= 0)
        {
            stem = stem[..underscore];
        }

        return stem.Length switch
        {
            2 => IsHexByte(stem),
            4 => IsEscapePair(stem[..2]) && IsHexByte(stem[2..]),
            6 => IsEscapePair(stem[..2]) && IsEscapePair(stem[2..4]) && IsHexByte(stem[4..]),
            _ => false,
        };
    }

    private static bool IsEscapePair(string text) =>
        text is "cb" or "ed" or "dd" or "fd";

    private static bool IsHexByte(string text) =>
        int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);

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

            // The FUSE port-read convention: an unseeded port returns the high
            // byte of its address.
            if (!cpu.IoRq && !cpu.Rd)
            {
                cpu.Data = (byte)(cpu.Address >> 8);
            }

            // A machine cycle's transfer is timestamped at the T-state it ends
            // on: T4 of an M1, T3 of a 3-T read or write. A port access is logged
            // one T-state into its I/O machine cycle, the edge FUSE records.
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

                case (Z80Chip.MachineCycleType.IoRead, Z80Chip.TState.T1):
                    trace.Add(new FuseBusEvent(t, "PR", cpu.Address, (byte)(cpu.Address >> 8)));
                    break;

                case (Z80Chip.MachineCycleType.IoWrite, Z80Chip.TState.T1):
                    trace.Add(new FuseBusEvent(t, "PW", cpu.Address, cpu.Data));
                    break;
            }

            // A generous bound over the case's own T-state budget catches a
            // microcode path that never returns to an instruction boundary
            // (the repeating block instructions are the long ones - LDIR over a
            // full BC runs into the hundreds).
            if (t > input.TStates + 200)
            {
                Assert.Fail(
                    $"{caseName}: instruction did not complete within {input.TStates + 200} T-states");
            }
        }

        // --- Bus-event trace -------------------------------------------------
        // Keep the real bus transfers; drop the MC / PC contention rows (see the
        // file header - board-level ULA behaviour, not a CPU action).
        var expectedTrace = expected.Events
            .Where(e => e.Kind is "MR" or "MW" or "PR" or "PW")
            .ToList();

        // Format() renders each event as "<time> <kind> <addr> <val>", so this
        // single whole-string comparison asserts, for every MR / MW / PR / PW:
        // the kind, the address, the byte, the FUSE <time> T-state stamp, the
        // order, and that there are no extra or missing events.
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
