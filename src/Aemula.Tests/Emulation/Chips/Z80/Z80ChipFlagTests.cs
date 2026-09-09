using System;
using System.Threading.Tasks;
using Aemula.Emulation.Chips.Z80;

namespace Aemula.Tests.Emulation.Chips.Z80;

// Hand-written flag-edge checks for the unprefixed 8-bit / 16-bit ALU, the
// accumulator rotates and DAA / CPL / SCF / CCF. The FUSE per-instruction
// vectors in Z80ChipBusTimingTests are the broad oracle; these pin the corners
// that are easy to get subtly wrong and quick to read when they break. Flag
// formulas follow "The Undocumented Z80 Documented" (Sean Young).
public class Z80ChipFlagTests
{
    // Runs a short program placed at 0x0000, stopping after the given number of
    // instruction boundaries, and returns the chip with its packed F byte
    // refreshed from the live flag fields.
    private static Z80Chip RunOne(Action<Z80Chip> setup, params byte[] program) =>
        Run(setup, 1, program);

    private static Z80Chip Run(Action<Z80Chip> setup, int instructions, params byte[] program)
    {
        var ram = new byte[0x10000];
        Array.Copy(program, ram, program.Length);

        var cpu = new Z80Chip();
        setup(cpu);

        var completed = 0;
        var wasAtBoundary = false;

        for (var guard = 0; guard < 400 && completed < instructions; guard++)
        {
            cpu.Clk = true;
            cpu.Clk = false;

            if (!cpu.MReq && !cpu.Rd)
            {
                cpu.Data = ram[cpu.Address];
            }

            if (!cpu.MReq && !cpu.Wr)
            {
                ram[cpu.Address] = cpu.Data;
            }

            if (cpu.AtInstructionBoundary && !wasAtBoundary)
            {
                completed++;
            }

            wasAtBoundary = cpu.AtInstructionBoundary;
        }

        cpu.AF.F = cpu.Flags.AsByte();
        return cpu;
    }

    private static void SetFlags(Z80Chip cpu, byte f) => cpu.Flags.SetFromByte(f);

    // --- 8-bit add / subtract -------------------------------------------

    [Test]
    public async Task AddSetsHalfCarryAndOverflow()
    {
        // 0x38 + 0x48 = 0x80: half-carry out of bit 3, and a +ve + +ve => -ve
        // signed overflow. No carry out of bit 7.
        var cpu = RunOne(c => { c.AF.A = 0x38; c.BC.B = 0x48; }, 0x80); // ADD A,B

        await Assert.That(cpu.AF.A).IsEqualTo((byte)0x80);
        await Assert.That(cpu.Flags.Sign).IsTrue();
        await Assert.That(cpu.Flags.Zero).IsFalse();
        await Assert.That(cpu.Flags.HalfCarry).IsTrue();
        await Assert.That(cpu.Flags.ParityOverflow).IsTrue();
        await Assert.That(cpu.Flags.Subtract).IsFalse();
        await Assert.That(cpu.Flags.Carry).IsFalse();
    }

    [Test]
    public async Task AdcFoldsInTheCarry()
    {
        var cpu = RunOne(
            c => { c.AF.A = 0x0F; c.BC.C = 0x00; SetFlags(c, 0x01); },
            0x89); // ADC A,C  with C(flag)=1

        await Assert.That(cpu.AF.A).IsEqualTo((byte)0x10);
        await Assert.That(cpu.Flags.HalfCarry).IsTrue();
        await Assert.That(cpu.Flags.Carry).IsFalse();
        await Assert.That(cpu.Flags.Subtract).IsFalse();
    }

    [Test]
    public async Task SbcFoldsInTheBorrow()
    {
        var cpu = RunOne(
            c => { c.AF.A = 0x00; c.DE.E = 0x00; SetFlags(c, 0x01); },
            0x9B); // SBC A,E  with C(flag)=1

        await Assert.That(cpu.AF.A).IsEqualTo((byte)0xFF);
        await Assert.That(cpu.Flags.Sign).IsTrue();
        await Assert.That(cpu.Flags.HalfCarry).IsTrue();
        await Assert.That(cpu.Flags.Subtract).IsTrue();
        await Assert.That(cpu.Flags.Carry).IsTrue();
    }

    [Test]
    public async Task CpTakesBits53FromTheOperandNotTheResult()
    {
        // A - n where n has bits 5 and 3 set but the difference does not.
        var cpu = RunOne(
            c => { c.AF.A = 0x00; },
            0xFE, 0x28); // CP 0x28

        await Assert.That(cpu.AF.A).IsEqualTo((byte)0x00).Because("CP does not store");
        await Assert.That(cpu.Flags.Y).IsTrue().Because("bit 5 of the operand 0x28");
        await Assert.That(cpu.Flags.X).IsTrue().Because("bit 3 of the operand 0x28");
        await Assert.That(cpu.Flags.Subtract).IsTrue();
        await Assert.That(cpu.Flags.Carry).IsTrue().Because("0x00 - 0x28 borrows");
    }

    // --- INC / DEC -------------------------------------------------------

    [Test]
    public async Task IncByPreservesCarryAndOverflowsAt0x7F()
    {
        var cpu = RunOne(
            c => { c.BC.B = 0x7F; SetFlags(c, 0x00); },
            0x04); // INC B

        await Assert.That(cpu.BC.B).IsEqualTo((byte)0x80);
        await Assert.That(cpu.Flags.Sign).IsTrue();
        await Assert.That(cpu.Flags.HalfCarry).IsTrue();
        await Assert.That(cpu.Flags.ParityOverflow).IsTrue().Because("0x7F -> 0x80");
        await Assert.That(cpu.Flags.Subtract).IsFalse();
        await Assert.That(cpu.Flags.Carry).IsFalse();
    }

    [Test]
    public async Task IncSetsCarryOnlyIfItWasAlreadySet()
    {
        var cpu = RunOne(
            c => { c.BC.B = 0xFF; SetFlags(c, 0x01); },
            0x04); // INC B, C(flag) preset

        await Assert.That(cpu.BC.B).IsEqualTo((byte)0x00);
        await Assert.That(cpu.Flags.Zero).IsTrue();
        await Assert.That(cpu.Flags.HalfCarry).IsTrue();
        await Assert.That(cpu.Flags.Carry).IsTrue().Because("INC leaves C untouched");
    }

    [Test]
    public async Task DecByOverflowsAt0x80()
    {
        var cpu = RunOne(
            c => { c.BC.B = 0x80; SetFlags(c, 0x00); },
            0x05); // DEC B

        await Assert.That(cpu.BC.B).IsEqualTo((byte)0x7F);
        await Assert.That(cpu.Flags.Sign).IsFalse();
        await Assert.That(cpu.Flags.HalfCarry).IsTrue().Because("borrow out of bit 4");
        await Assert.That(cpu.Flags.ParityOverflow).IsTrue().Because("0x80 -> 0x7F");
        await Assert.That(cpu.Flags.Subtract).IsTrue();
    }

    // --- logical ------------------------------------------------------

    [Test]
    public async Task AndSetsHalfCarryOrAndXorClearIt()
    {
        var and = RunOne(c => { c.AF.A = 0xFF; c.BC.B = 0x0F; }, 0xA0); // AND B
        await Assert.That(and.Flags.HalfCarry).IsTrue();
        await Assert.That(and.Flags.Carry).IsFalse();
        await Assert.That(and.Flags.ParityOverflow).IsTrue().Because("0x0F has even parity");

        var or = RunOne(c => { c.AF.A = 0x01; c.BC.B = 0x02; }, 0xB0); // OR B
        await Assert.That(or.Flags.HalfCarry).IsFalse();

        var xor = RunOne(c => { c.AF.A = 0xFF; c.BC.B = 0xFF; }, 0xA8); // XOR B
        await Assert.That(xor.Flags.HalfCarry).IsFalse();
        await Assert.That(xor.Flags.Zero).IsTrue();
        await Assert.That(xor.Flags.ParityOverflow).IsTrue();
    }

    // --- DAA ----------------------------------------------------------

    [Test]
    [Arguments(0x1F, 0x00, 0x25, true, false)]   // after ADD: low nibble > 9 -> +0x06, H set
    [Arguments(0x9A, 0x00, 0x00, true, true)]    // after ADD: both nibbles carry -> 0x00, H+C
    [Arguments(0x00, 0x12, 0xFA, true, false)]   // after SUB (N,H set via F=0x12): -0x06 -> 0xFA
    public async Task DaaCorners(int a, int f, int expectedA, bool expectedHalf, bool expectedCarry)
    {
        var cpu = RunOne(
            c => { c.AF.A = (byte)a; SetFlags(c, (byte)f); },
            0x27); // DAA

        await Assert.That(cpu.AF.A).IsEqualTo((byte)expectedA);
        await Assert.That(cpu.Flags.HalfCarry).IsEqualTo(expectedHalf);
        await Assert.That(cpu.Flags.Carry).IsEqualTo(expectedCarry);
        await Assert.That(cpu.Flags.ParityOverflow).IsEqualTo(Z80Chip.ParityTable[expectedA]);
    }

    // --- CPL / SCF / CCF -------------------------------------------

    [Test]
    public async Task CplComplementsAndSetsHalfCarryAndSubtract()
    {
        var cpu = RunOne(c => { c.AF.A = 0x28; SetFlags(c, 0x00); }, 0x2F); // CPL

        await Assert.That(cpu.AF.A).IsEqualTo((byte)0xD7);
        await Assert.That(cpu.Flags.HalfCarry).IsTrue();
        await Assert.That(cpu.Flags.Subtract).IsTrue();
        await Assert.That(cpu.Flags.Y).IsFalse().Because("bit 5 of the new A 0xD7 is 0");
        await Assert.That(cpu.Flags.X).IsFalse().Because("bit 3 of the new A 0xD7 is 0");
    }

    [Test]
    public async Task ScfWithNoPriorFlagWriteTakesBits53FromAOrF()
    {
        // Q starts at 0, so bits 5/3 of F become (F_before | A) at those spots.
        var cpu = RunOne(c => { c.AF.A = 0x00; SetFlags(c, 0xFF); }, 0x37); // SCF

        await Assert.That(cpu.Flags.Carry).IsTrue();
        await Assert.That(cpu.Flags.HalfCarry).IsFalse();
        await Assert.That(cpu.Flags.Subtract).IsFalse();
        await Assert.That(cpu.AF.F).IsEqualTo((byte)0xED).Because("F_before 0xFF | A 0x00 keeps bits 5/3");
    }

    [Test]
    public async Task ScfBits53FollowAWhenFIsClear()
    {
        var cpu = RunOne(c => { c.AF.A = 0xFF; SetFlags(c, 0x00); }, 0x37); // SCF

        await Assert.That(cpu.Flags.Y).IsTrue();
        await Assert.That(cpu.Flags.X).IsTrue();
        await Assert.That(cpu.AF.F).IsEqualTo((byte)0x29);
    }

    [Test]
    public async Task ScfBits53UseTheQLatchFromThePrecedingAluOp()
    {
        // ADD A,B: 0x08 + 0x08 = 0x10, leaving F = 0x10 (bits 5/3 clear), so
        // Q = 0x10. The following SCF forms bits 5/3 from (Q ^ F_before) | A =
        // (0x10 ^ 0x10) | 0x10 = 0x10, whose bits 5 and 3 are both clear.
        var cpu = Run(
            c => { c.AF.A = 0x08; c.BC.B = 0x08; },
            2,
            0x80, 0x37); // ADD A,B ; SCF

        await Assert.That(cpu.AF.A).IsEqualTo((byte)0x10);
        await Assert.That(cpu.Flags.Carry).IsTrue();
        await Assert.That(cpu.Flags.Y).IsFalse();
        await Assert.That(cpu.Flags.X).IsFalse();
    }

    [Test]
    public async Task CcfPutsOldCarryIntoHalfCarryAndTogglesCarry()
    {
        var set = RunOne(c => { c.AF.A = 0x00; SetFlags(c, 0x01); }, 0x3F); // CCF, C=1
        await Assert.That(set.Flags.Carry).IsFalse();
        await Assert.That(set.Flags.HalfCarry).IsTrue().Because("H <- old C");
        await Assert.That(set.Flags.Subtract).IsFalse();

        var clear = RunOne(c => { c.AF.A = 0x00; SetFlags(c, 0x00); }, 0x3F); // CCF, C=0
        await Assert.That(clear.Flags.Carry).IsTrue();
        await Assert.That(clear.Flags.HalfCarry).IsFalse();
    }

    // --- 16-bit ADD HL,ss --------------------------------------

    [Test]
    public async Task AddHlHalfCarryComesFromBit11()
    {
        // 0x0800 + 0x0800 = 0x1000: carry out of bit 11, none out of bit 15.
        var cpu = RunOne(
            c => { c.HL.Value = 0x0800; c.BC.Value = 0x0800; SetFlags(c, 0xFF); },
            0x09); // ADD HL,BC

        await Assert.That(cpu.HL.Value).IsEqualTo((ushort)0x1000);
        await Assert.That(cpu.Flags.HalfCarry).IsTrue();
        await Assert.That(cpu.Flags.Carry).IsFalse();
        await Assert.That(cpu.Flags.Subtract).IsFalse();
        // S, Z and P/V are carried through untouched from F = 0xFF.
        await Assert.That(cpu.Flags.Sign).IsTrue();
        await Assert.That(cpu.Flags.Zero).IsTrue();
        await Assert.That(cpu.Flags.ParityOverflow).IsTrue();
        await Assert.That(cpu.WZ.Value).IsEqualTo((ushort)0x0801).Because("WZ = HL + 1");
    }

    [Test]
    public async Task AddHlCarryComesFromBit15AndBits53FromTheResultHighByte()
    {
        var cpu = RunOne(
            c => { c.HL.Value = 0xF000; c.DE.Value = 0x3000; SetFlags(c, 0x00); },
            0x19); // ADD HL,DE

        await Assert.That(cpu.HL.Value).IsEqualTo((ushort)0x2000);
        await Assert.That(cpu.Flags.Carry).IsTrue();
        await Assert.That(cpu.Flags.Y).IsTrue().Because("bit 13 of the result set");
        await Assert.That(cpu.Flags.X).IsFalse();
    }

    // --- accumulator rotates ----------------------------------

    [Test]
    public async Task RlcaRotatesLeftThroughBit7IntoCarryAndBit0()
    {
        var cpu = RunOne(c => { c.AF.A = 0x88; SetFlags(c, 0xFF); }, 0x07); // RLCA

        await Assert.That(cpu.AF.A).IsEqualTo((byte)0x11);
        await Assert.That(cpu.Flags.Carry).IsTrue();
        await Assert.That(cpu.Flags.HalfCarry).IsFalse();
        await Assert.That(cpu.Flags.Subtract).IsFalse();
        await Assert.That(cpu.Flags.Sign).IsTrue().Because("S/Z/P/V pass through");
        await Assert.That(cpu.Flags.Zero).IsTrue();
    }

    [Test]
    public async Task RraRotatesRightThroughCarry()
    {
        var cpu = RunOne(c => { c.AF.A = 0x01; SetFlags(c, 0x00); }, 0x1F); // RRA, C=0

        await Assert.That(cpu.AF.A).IsEqualTo((byte)0x00);
        await Assert.That(cpu.Flags.Carry).IsTrue().Because("old bit 0");
        await Assert.That(cpu.Flags.HalfCarry).IsFalse();
        await Assert.That(cpu.Flags.Subtract).IsFalse();
    }

    [Test]
    public async Task RlaShiftsCarryIntoBit0()
    {
        var cpu = RunOne(c => { c.AF.A = 0x80; SetFlags(c, 0x01); }, 0x17); // RLA, C=1

        await Assert.That(cpu.AF.A).IsEqualTo((byte)0x01);
        await Assert.That(cpu.Flags.Carry).IsTrue().Because("old bit 7");
    }

    // --- phase boundary: NEG is an ED opcode -----------------

    [Test]
    public async Task NegIsNotDecodedYet()
    {
        // NEG is ED 44 - it belongs with the ED table, not the unprefixed core.
        await Assert.That(() => RunOne(c => { c.AF.A = 0x01; }, 0xED, 0x44))
            .Throws<NotImplementedException>();
    }
}
