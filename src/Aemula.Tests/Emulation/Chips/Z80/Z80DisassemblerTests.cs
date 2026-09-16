using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Aemula.Debugging;
using Aemula.Emulation.Chips.Z80.Debugging;

namespace Aemula.Tests.Emulation.Chips.Z80;

public class Z80DisassemblerTests
{
    // Mnemonics with no more specific MnemonicCategory than Other - anything
    // else reporting Other means a switch arm in Z80Disassembler forgot to
    // pass a real mnemonic (which DoHelper's MnemonicCategories lookup would
    // otherwise have thrown on for an unrecognized one, but not for a
    // recognized-but-wrong one).
    private static readonly HashSet<string> OtherCategoryMnemonics = ["NOP", "HALT", "NOP*", "DB"];

    [Test]
    public async Task AllOpcodesHaveMnemonicAndCategorization()
    {
        // Base (unprefixed) table, including CB/ED/DD/FD escapes.
        for (var opcode = 0; opcode <= 0xFF; opcode++)
        {
            await AssertCategorized((byte)opcode);
        }

        // CB-prefixed page.
        for (var sub = 0; sub <= 0xFF; sub++)
        {
            await AssertCategorized(0xCB, (byte)sub);
        }

        // ED-prefixed page.
        for (var sub = 0; sub <= 0xFF; sub++)
        {
            await AssertCategorized(0xED, (byte)sub);
        }

        // DD/FD-prefixed page, including the DD CB / FD CB sub-page.
        foreach (var prefix in new byte[] { 0xDD, 0xFD })
        {
            for (var sub = 0; sub <= 0xFF; sub++)
            {
                if (sub == 0xCB)
                {
                    for (var op = 0; op <= 0xFF; op++)
                    {
                        await AssertCategorized(prefix, 0xCB, 0x00, (byte)op);
                    }
                }
                else
                {
                    await AssertCategorized(prefix, (byte)sub);
                }
            }
        }
    }

    private static async Task AssertCategorized(params byte[] instructionBytes)
    {
        var instruction = DisassembleAt(instructionBytes);

        await Assert.That(instruction.Mnemonic).IsNotEmpty();

        if (!OtherCategoryMnemonics.Contains(instruction.Mnemonic))
        {
            await Assert.That(instruction.MnemonicCategory).IsNotEqualTo(MnemonicCategory.Other);
        }

        if (instruction.Operand.Length == 0)
        {
            await Assert.That(instruction.OperandKind).IsEqualTo(OperandKind.None);
        }
        // "RET cc" is the one shape whose only operand is a bare condition
        // code (NZ/Z/NC/C/PO/PE/P/M) - not a register, address, immediate or
        // indirection, so OperandKind.None is the correct tag here, not a
        // forgotten one. Every other conditional mnemonic (JP/CALL cc, ...)
        // always pairs the condition with a real address operand.
        else if (instruction.Mnemonic != "RET")
        {
            await Assert.That(instruction.OperandKind).IsNotEqualTo(OperandKind.None);
        }
    }

    // Splitting Mnemonic/Operand out of one pre-joined string (see
    // Z80Disassembler's class remarks) should never change what actually
    // renders - Disassembly reassembles them, so it should read back
    // byte-for-byte identical to the combined text every one of these call
    // sites produced before the split. One representative case per switch
    // arm shape across the base/CB/ED/DD/DD-CB tables, plus one FD case to
    // confirm the IX/IY substitution still threads through correctly.
    [Test]
    public async Task DisassemblyTextMatchesExpectedAcrossEveryPage()
    {
        // Base table.
        await AssertDisassembly("LD BC, 0x1234", 0x01, 0x34, 0x12);
        await AssertDisassembly("LD (HL), 0x56", 0x36, 0x56);
        await AssertDisassembly("LD A, (HL)", 0x7E);
        await AssertDisassembly("ADD A, (HL)", 0x86);
        await AssertDisassembly("JP NZ, 0x1234", 0xC2, 0x34, 0x12);
        await AssertDisassembly("RET NZ", 0xC0);

        // CB page.
        await AssertDisassembly("RLC B", 0xCB, 0x00);
        await AssertDisassembly("BIT 0, (HL)", 0xCB, 0x46);
        await AssertDisassembly("SET 0, (HL)", 0xCB, 0xC6);

        // ED page.
        await AssertDisassembly("IN B, (C)", 0xED, 0x40);
        await AssertDisassembly("IN (C)", 0xED, 0x70);
        await AssertDisassembly("LD (0x7676), BC", 0xED, 0x43);
        await AssertDisassembly("NEG", 0xED, 0x44);
        await AssertDisassembly("RETN", 0xED, 0x45);
        await AssertDisassembly("RETI", 0xED, 0x4D);
        await AssertDisassembly("IM 0", 0xED, 0x46);
        await AssertDisassembly("LD I, A", 0xED, 0x47);
        await AssertDisassembly("RRD", 0xED, 0x67);
        await AssertDisassembly("LDIR", 0xED, 0xB0);
        await AssertDisassembly("OTIR", 0xED, 0xB3);
        await AssertDisassembly("OUTI", 0xED, 0xA3);
        await AssertDisassembly("DB 0xED, 0xFF", 0xED, 0xFF);

        // DD (IX) page.
        await AssertDisassembly("LD IX, 0x7676", 0xDD, 0x21);
        await AssertDisassembly("LD (0x7676), IX", 0xDD, 0x22);
        await AssertDisassembly("INC IX", 0xDD, 0x23);
        await AssertDisassembly("INC (IX+0x76)", 0xDD, 0x34);
        await AssertDisassembly("LD B, (IX+0x76)", 0xDD, 0x46);
        await AssertDisassembly("LD (IX+0x76), B", 0xDD, 0x70);
        await AssertDisassembly("ADD IX, BC", 0xDD, 0x09);
        await AssertDisassembly("ADD A, (IX+0x76)", 0xDD, 0x86);
        await AssertDisassembly("SBC A, (IX+0x76)", 0xDD, 0x9E);
        await AssertDisassembly("LD (IX+0x76), 0x76", 0xDD, 0x36);
        await AssertDisassembly("LD IXH, 0x76", 0xDD, 0x26);
        await AssertDisassembly("JP (IX)", 0xDD, 0xE9);
        await AssertDisassembly("DB 0xDD (IX prefix)", 0xDD, 0xDD);

        // DD CB page.
        await AssertDisassembly("RLC (IX+0x05)", 0xDD, 0xCB, 0x05, 0x06);
        await AssertDisassembly("BIT 0, (IX+0x05)", 0xDD, 0xCB, 0x05, 0x46);
        await AssertDisassembly("RLC (IX+0x05), B", 0xDD, 0xCB, 0x05, 0x00);

        // FD (IY) page - confirms the substitution threads through, not just IX.
        await AssertDisassembly("LD IY, 0x7676", 0xFD, 0x21);
    }

    private static async Task AssertDisassembly(string expected, params byte[] instructionBytes)
    {
        await Assert.That(DisassembleAt(instructionBytes).Disassembly).IsEqualTo(expected);
    }

    private static DisassembledInstruction DisassembleAt(params byte[] instructionBytes)
    {
        // The whole address space is HALT (hasNext: false) except for the
        // bytes under test, so OnAddressExecuting's forward/jump-target
        // chase (see Disassembler.DisassembleAddresses) always terminates
        // in at most one extra hop, regardless of what operand bytes or
        // jump targets the instruction under test happens to compute.
        var memory = new byte[0x10000];
        Array.Fill(memory, (byte)0x76);
        instructionBytes.CopyTo(memory, 0);

        var memoryCallbacks = new DebuggerMemoryCallbacks(
            address => memory[address],
            (address, value) => memory[address] = value);

        var disassembler = new Z80Disassembler(memoryCallbacks);
        disassembler.OnAddressExecuting(0);

        return disassembler.Cache[0].Instruction!.Value;
    }
}
