using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Aemula.Emulation.Chips.Z80;

namespace Aemula.Tests.Emulation.Chips.Z80;

// Whole-program conformance runners for the Z80.
//
// Two harnesses live here:
//
//   RunCpmExerciser  - loads a CP/M ".com" at 0x0100 with PC = 0x0100, patches
//                      an OUT-based trap at BDOS (0x0005) and warm boot
//                      (0x0000), and clocks the chip one T-state at a time,
//                      servicing memory off /MREQ + /RD // /WR and catching the
//                      trap OUT on /IORQ + /WR. It implements BDOS function 2
//                      (character in E) and function 9 ($-terminated string at
//                      DE). Same shape as Intel8080ChipTests.Test8080.
//
//   RunSpectrumTest  - parses a ZX Spectrum ".tap" tape image, loads its CODE
//                      block at the address its header names (0x8000 for the
//                      z80test suite), enters at that address with a sentinel
//                      return on the stack, stubs the handful of 48K ROM entry
//                      points the z80test driver calls (RST 0x10 print-a-char,
//                      0x1601 CHAN-OPEN) and makes IN (0xFE) read 0xBF, and
//                      streams the characters passed to RST 0x10.
//
// Test-ROM provenance (all committed under Assets/):
//   zexdoc.com / zexall.com - Frank Cringle's Z80 instruction exerciser,
//     documented- and all-flags variants, from the widely mirrored copy at
//     github.com/anotherlin/z80emu/tree/master/testfiles.
//   z80flags.tap / z80docflags.tap / z80doc.tap / z80full.tap / z80ccf.tap /
//   z80memptr.tap - Patrik Rak's "z80test" suite v1.2a, from its GitHub release
//     github.com/raxoft/z80test (MIT; z80test-license.txt sits alongside). The
//     suite ships only as ZX Spectrum tape images - there is no CP/M .com build
//     of it anywhere - hence the TAP shim.
//
// None of these run to completion yet, and every row below is [Skip]ped. The
// unprefixed, CB and ED instruction groups are all decoded now - and gated by
// Z80ChipBusTimingTests (the FUSE per-instruction bus + register + flag +
// MEMPTR vectors, every unprefixed / cb xx / ed xx case) and Z80ChipFlagTests
// (hand-written flag-edge cases). What still blocks every ROM here is the
// DD/FD (IX/IY) group: it is not just the test bodies that use it - the
// zexdoc/zexall scaffolding sets up each subtest with FD-prefixed loads, and
// the z80test driver pushes IY, so neither harness reaches its first "OK"
// line without index-prefix decode. Switch these on once DD/FD lands:
//   * zexdoc.com to a full pass, then zexall.com (the undocumented Y/X flags);
//     z80flags.tap / z80doc.tap / z80docflags.tap should pass too.
//   * z80full.tap, z80ccf.tap (the SCF/CCF bit 3/5 corner) and z80memptr.tap
//     (WZ observed through BIT n,(IX+d)).
// When a ROM passes, drop its [Skip], and add an exact total-T-state assertion
// captured from a reference core as the timing ratchet. The maxTStates caps
// below are already generous-but-bounded for a full conformance run.
public class Z80ChipTests
{
    private static readonly string AssetsPath =
        Path.Combine("Emulation", "Chips", "Z80", "Assets");

    [Test]
    [Skip("Needs the DD/FD decode group: the exerciser scaffolds every subtest with FD-prefixed loads, so it throws before the first 'OK'. CB and ED are done (covered by Z80ChipBusTimingTests).")]
    [Arguments("zexdoc.com")]
    [Arguments("zexall.com")]
    public async Task CpmExerciser(string fileName)
    {
        var result = RunCpmExerciser(Path.Combine(AssetsPath, fileName), maxTStates: 6_000_000_000);

        Context.Current.OutputWriter.Write(result.Output);

        await Assert.That(result.Completed).IsTrue().Because("exerciser signalled warm boot");
        await Assert.That(result.Output).DoesNotContain("ERROR");
    }

    [Test]
    [Skip("Needs the DD/FD decode group: the z80test driver uses PUSH IY (FD E5) and every subtest body covers IX/IY opcodes. ED (LDIR etc.) is done (covered by Z80ChipBusTimingTests).")]
    [Arguments("z80docflags.tap")]
    [Arguments("z80flags.tap")]
    [Arguments("z80doc.tap")]
    [Arguments("z80full.tap")]
    [Arguments("z80ccf.tap")]
    [Arguments("z80memptr.tap")]
    public async Task Z80Test(string fileName)
    {
        var result = RunSpectrumTest(Path.Combine(AssetsPath, fileName), maxTStates: 6_000_000_000);

        Context.Current.OutputWriter.Write(result.Output);

        await Assert.That(result.Completed).IsTrue().Because("driver returned to its caller");
        await Assert.That(result.Output).Contains("all tests passed");
    }

    // --- CP/M ".com" harness ------------------------------------------------

    internal readonly record struct RunResult(string Output, long TStates, bool Completed);

    internal static RunResult RunCpmExerciser(string path, long maxTStates)
    {
        var program = File.ReadAllBytes(path);

        var ram = new byte[0x10000];
        Array.Copy(program, 0, ram, 0x100, program.Length);

        // Warm boot: OUT (0),A - the exerciser jumps here when finished.
        ram[0x0000] = 0xD3;
        ram[0x0001] = 0x00;

        // BDOS: OUT (1),A then RET - the exerciser calls 0x0005 for console I/O.
        ram[0x0005] = 0xD3;
        ram[0x0006] = 0x01;
        ram[0x0007] = 0xC9;

        var cpu = new Z80Chip();
        cpu.PC.Value = 0x0100;
        cpu.Flags.SetFromByte(cpu.AF.F);

        var output = new StringBuilder();
        var completed = false;
        long t = 0;

        while (t < maxTStates && !completed)
        {
            cpu.Clk = true;
            cpu.Clk = false;
            t++;

            if (!cpu.MReq && !cpu.Rd)
            {
                cpu.Data = ram[cpu.Address];
            }

            if (!cpu.MReq && !cpu.Wr)
            {
                ram[cpu.Address] = cpu.Data;
            }

            if (!cpu.IoRq && !cpu.Rd)
            {
                cpu.Data = (byte)(cpu.Address >> 8);
            }

            if (!cpu.IoRq && !cpu.Wr)
            {
                switch (cpu.Address & 0xFF)
                {
                    case 0x00:
                        completed = true;
                        break;

                    case 0x01:
                        ServiceBdos(cpu, ram, output);
                        break;
                }
            }
        }

        return new RunResult(output.ToString(), t, completed);
    }

    private static void ServiceBdos(Z80Chip cpu, byte[] ram, StringBuilder output)
    {
        switch (cpu.BC.C)
        {
            case 0x02: // console output - character in E
                output.Append((char)cpu.DE.E);
                break;

            case 0x09: // print string - $-terminated, address in DE
                var address = cpu.DE.Value;
                while (ram[address] != '$')
                {
                    output.Append((char)ram[address++]);
                }
                break;
        }
    }

    // --- ZX Spectrum ".tap" shim -----------------------------------------

    internal static RunResult RunSpectrumTest(string path, long maxTStates)
    {
        var tap = File.ReadAllBytes(path);
        var (loadAddress, code) = ExtractTapCodeBlock(tap);

        var ram = new byte[0x10000];
        Array.Copy(code, 0, ram, loadAddress, code.Length);

        // 48K ROM entry points the z80test driver calls: a bare RET at each is
        // all it needs from them (the printed character is caught off the M1
        // fetch of the RST 0x10 vector; CHAN-OPEN just has to return).
        ram[0x0010] = 0xC9; // RST 0x10 - print the character in A
        ram[0x1601] = 0xC9; // CHAN-OPEN

        // The driver enters as "RANDOMIZE USR <load>" and finishes with a plain
        // RET; land that on a sentinel address that is otherwise never executed.
        const ushort sentinel = 0x3FFF;

        var cpu = new Z80Chip();
        cpu.PC.Value = (ushort)loadAddress;
        cpu.SP.Value = 0xFF00;
        ram[0xFF00] = sentinel & 0xFF;
        ram[0xFF01] = sentinel >> 8;
        cpu.Flags.SetFromByte(cpu.AF.F);

        var output = new StringBuilder();
        var completed = false;
        long t = 0;

        while (t < maxTStates && !completed)
        {
            cpu.Clk = true;
            cpu.Clk = false;
            t++;

            var opcodeFetch = !cpu.M1 && !cpu.MReq && !cpu.Rd;

            if (!cpu.MReq && !cpu.Rd)
            {
                cpu.Data = ram[cpu.Address];
            }

            if (!cpu.MReq && !cpu.Wr)
            {
                ram[cpu.Address] = cpu.Data;
            }

            if (!cpu.IoRq && !cpu.Rd)
            {
                // The driver's .incheck reads IN (0xFE) and wants 0xBF (no key,
                // MIC low); anything else it reports as an "IN FE" failure.
                cpu.Data = (cpu.Address & 0xFF) == 0xFE
                    ? (byte)0xBF
                    : (byte)(cpu.Address >> 8);
            }

            if (opcodeFetch && cpu.Address == 0x0010)
            {
                AppendSpectrumChar(output, cpu.AF.A);
            }

            if (opcodeFetch && cpu.Address == sentinel)
            {
                completed = true;
            }
        }

        return new RunResult(output.ToString(), t, completed);
    }

    // A .tap block is [u16 length][payload]; payload[0] is the block flag
    // (0x00 header, 0xFF data) and the last payload byte is an XOR checksum. A
    // type-3 (CODE) header carries the load length at bytes 12-13 and the load
    // address at bytes 14-15, and is followed by the matching data block.
    private static (int LoadAddress, byte[] Code) ExtractTapCodeBlock(byte[] tap)
    {
        var pos = 0;
        var codeLength = 0;
        var loadAddress = 0;
        var haveHeader = false;

        while (pos + 2 <= tap.Length)
        {
            var length = tap[pos] | (tap[pos + 1] << 8);
            pos += 2;
            if (length < 2 || pos + length > tap.Length)
            {
                break;
            }

            var flag = tap[pos];

            if (flag == 0x00 && length >= 19 && tap[pos + 1] == 0x03)
            {
                // Header payload: flag, type, 10-byte name, then u16 length and
                // u16 load address.
                codeLength = tap[pos + 12] | (tap[pos + 13] << 8);
                loadAddress = tap[pos + 14] | (tap[pos + 15] << 8);
                haveHeader = true;
            }
            else if (flag == 0xFF && haveHeader)
            {
                var code = new byte[codeLength];
                Array.Copy(tap, pos + 1, code, 0, codeLength);
                return (loadAddress, code);
            }

            pos += length;
        }

        throw new InvalidDataException("no CODE block found in the .tap image");
    }

    // RST 0x10 takes a Spectrum character code in A. Control codes 6-21 consume
    // following argument bytes on real hardware; for the tester's output only
    // 13 (newline) and the printable range matter, so the rest are dropped.
    private static void AppendSpectrumChar(StringBuilder output, byte code)
    {
        if (code == 13)
        {
            output.Append('\n');
        }
        else if (code >= 32 && code < 127)
        {
            output.Append((char)code);
        }
    }
}
