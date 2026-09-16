using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Aemula.Debugging;
using Aemula.Emulation.Chips.Intel8080.Debugging;

namespace Aemula.Tests.Emulation.Chips.Intel8080;

public class Intel8080DisassemblerTests
{
    // Real 8080 opcode slots this disassembler doesn't implement (mirrors of
    // other opcodes on real silicon) - pre-existing, unrelated to the
    // mnemonic/operand split, so left throwing exactly as before.
    private static readonly HashSet<byte> UnimplementedOpcodes =
        [0x08, 0x10, 0x18, 0x20, 0x28, 0x30, 0x38, 0xCB, 0xD9, 0xDD, 0xED, 0xFD];

    // Mnemonics with no more specific MnemonicCategory than Other - anything
    // else reporting Other means a switch arm forgot to pass a real mnemonic
    // (which DoHelper's MnemonicCategories lookup would otherwise have
    // thrown on for an unrecognized one, but not for a recognized-but-wrong
    // one).
    private static readonly HashSet<string> OtherCategoryMnemonics = ["NOP", "HLT"];

    [Test]
    public async Task AllOpcodesHaveMnemonicAndCategorization()
    {
        for (var opcode = 0; opcode <= 0xFF; opcode++)
        {
            if (UnimplementedOpcodes.Contains((byte)opcode))
            {
                continue;
            }

            var instruction = DisassembleAt((byte)opcode);

            await Assert.That(instruction.Mnemonic).IsNotEmpty();

            if (!OtherCategoryMnemonics.Contains(instruction.Mnemonic))
            {
                await Assert.That(instruction.MnemonicCategory).IsNotEqualTo(MnemonicCategory.Other);
            }

            if (instruction.Operand.Length == 0)
            {
                await Assert.That(instruction.OperandKind).IsEqualTo(OperandKind.None);
            }
            else
            {
                await Assert.That(instruction.OperandKind).IsNotEqualTo(OperandKind.None);
            }
        }
    }

    // Splitting Mnemonic/Operand out of one pre-joined string should never
    // change what actually renders - Disassembly reassembles them, so it
    // should read back byte-for-byte identical to the combined text every
    // one of these call sites produced before the split.
    [Test]
    public async Task DisassemblyTextMatchesExpectedAcrossOpcodeShapes()
    {
        await AssertDisassembly("LXI B, 0x1234", 0x01, 0x34, 0x12);
        await AssertDisassembly("STAX B", 0x02);
        await AssertDisassembly("MVI M, 0x56", 0x36, 0x56);
        await AssertDisassembly("MOV B, M", 0x46);
        await AssertDisassembly("MOV M, B", 0x70);
        await AssertDisassembly("MOV A, A", 0x7F);
        await AssertDisassembly("ADD M", 0x86);
        await AssertDisassembly("CMP A", 0xBF);
        await AssertDisassembly("SHLD 0x1234", 0x22, 0x34, 0x12);
        await AssertDisassembly("STA 0x1234", 0x32, 0x34, 0x12);
        await AssertDisassembly("JNZ 0x1234", 0xC2, 0x34, 0x12);
        await AssertDisassembly("CALL 0x1234", 0xCD, 0x34, 0x12);
        await AssertDisassembly("RST 0", 0xC7);
        await AssertDisassembly("OUT 0x12", 0xD3, 0x12);
        await AssertDisassembly("IN 0x12", 0xDB, 0x12);
        await AssertDisassembly("POP PSW", 0xF1);
        await AssertDisassembly("PUSH PSW", 0xF5);
        await AssertDisassembly("CPI 0x12", 0xFE, 0x12);
        await AssertDisassembly("RNZ", 0xC0);
        await AssertDisassembly("PCHL", 0xE9);
    }

    private static async Task AssertDisassembly(string expected, params byte[] instructionBytes)
    {
        await Assert.That(DisassembleAt(instructionBytes).Disassembly).IsEqualTo(expected);
    }

    private static DisassembledInstruction DisassembleAt(params byte[] instructionBytes)
    {
        // The whole address space is HLT (hasNext: false) except for the
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

        var disassembler = new Intel8080Disassembler(memoryCallbacks);
        disassembler.OnAddressExecuting(0);

        return disassembler.Cache[0].Instruction!.Value;
    }
}
