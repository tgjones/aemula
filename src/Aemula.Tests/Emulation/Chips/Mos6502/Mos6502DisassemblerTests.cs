using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Aemula.Debugging;
using Aemula.Emulation.Chips.Mos6502;
using Aemula.Emulation.Chips.Mos6502.Debugging;

namespace Aemula.Tests.Emulation.Chips.Mos6502;

public class Mos6502DisassemblerTests
{
    [Test]
    public async Task AllOpcodesHaveMnemonicAndCategorization()
    {
        for (var opcode = 0; opcode <= 0xFF; opcode++)
        {
            var bytesAtAddress = new byte[] { (byte)opcode, 0x00, 0x00 };

            var instruction = Mos6502Chip.DisassembleInstruction(
                0,
                address => bytesAtAddress[address],
                []);

            await Assert.That(instruction.Mnemonic).IsNotEmpty();

            // JAM and NOP are the only mnemonics without a more specific category -
            // anything else reporting Other means a new opcode was added to
            // Mos6502CodeGenerator.Instructions without a matching entry in
            // MnemonicCategories (which the generator would otherwise have thrown
            // on at codegen time, but that's cheap insurance against it silently
            // stopping being exhaustive some other way).
            if (instruction.Mnemonic is not ("JAM" or "NOP"))
            {
                await Assert.That(instruction.MnemonicCategory).IsNotEqualTo(MnemonicCategory.Other);
            }

            // An empty Operand always pairs with OperandKind.None (accumulator/
            // implied addressing); a non-empty Operand should always carry a real
            // kind, or DisassemblyWindow's operand coloring silently falls back to
            // plain text for it.
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

    [Test]
    public async Task TryGetEffectiveAddressResolvesZeroPageX()
    {
        var memory = new byte[0x10000];
        memory[0x1000] = 0xB5; // LDA $10,X
        memory[0x1001] = 0x10;
        memory[0x0015] = 0xAA; // $10 + X(5) = $15

        var result = ResolveEffectiveAddress(memory, x: 0x05, y: 0x00, address: 0x1000);

        await Assert.That(result).IsEqualTo(((ushort)0x0015, (byte)0xAA));
    }

    [Test]
    public async Task TryGetEffectiveAddressResolvesZeroPageY()
    {
        var memory = new byte[0x10000];
        memory[0x1000] = 0xB6; // LDX $10,Y
        memory[0x1001] = 0x10;
        memory[0x0020] = 0xBB; // $10 + Y(0x10) = $20

        var result = ResolveEffectiveAddress(memory, x: 0x00, y: 0x10, address: 0x1000);

        await Assert.That(result).IsEqualTo(((ushort)0x0020, (byte)0xBB));
    }

    [Test]
    public async Task TryGetEffectiveAddressResolvesAbsoluteX()
    {
        var memory = new byte[0x10000];
        memory[0x1000] = 0xBD; // LDA $2000,X
        memory[0x1001] = 0x00;
        memory[0x1002] = 0x20;
        memory[0x2005] = 0xCC; // $2000 + X(5) = $2005

        var result = ResolveEffectiveAddress(memory, x: 0x05, y: 0x00, address: 0x1000);

        await Assert.That(result).IsEqualTo(((ushort)0x2005, (byte)0xCC));
    }

    [Test]
    public async Task TryGetEffectiveAddressResolvesAbsoluteY()
    {
        var memory = new byte[0x10000];
        memory[0x1000] = 0xB9; // LDA $2000,Y
        memory[0x1001] = 0x00;
        memory[0x1002] = 0x20;
        memory[0x2010] = 0xDD; // $2000 + Y(0x10) = $2010

        var result = ResolveEffectiveAddress(memory, x: 0x00, y: 0x10, address: 0x1000);

        await Assert.That(result).IsEqualTo(((ushort)0x2010, (byte)0xDD));
    }

    [Test]
    public async Task TryGetEffectiveAddressResolvesIndexedIndirectX()
    {
        var memory = new byte[0x10000];
        memory[0x1000] = 0xA1; // LDA ($10,X)
        memory[0x1001] = 0x10;
        memory[0x0015] = 0x00; // pointer low, at $10 + X(5) = $15
        memory[0x0016] = 0x30; // pointer high
        memory[0x3000] = 0xEE;

        var result = ResolveEffectiveAddress(memory, x: 0x05, y: 0x00, address: 0x1000);

        await Assert.That(result).IsEqualTo(((ushort)0x3000, (byte)0xEE));
    }

    [Test]
    public async Task TryGetEffectiveAddressResolvesIndirectIndexedY()
    {
        var memory = new byte[0x10000];
        memory[0x1000] = 0xB1; // LDA ($10),Y
        memory[0x1001] = 0x10;
        memory[0x0010] = 0x00; // pointer low, at $10 directly (not indexed)
        memory[0x0011] = 0x40; // pointer high
        memory[0x4010] = 0xFF; // $4000 + Y(0x10) = $4010

        var result = ResolveEffectiveAddress(memory, x: 0x00, y: 0x10, address: 0x1000);

        await Assert.That(result).IsEqualTo(((ushort)0x4010, (byte)0xFF));
    }

    [Test]
    public async Task TryGetEffectiveAddressReplicatesJmpIndirectPageWrapBug()
    {
        var memory = new byte[0x10000];
        memory[0x1000] = 0x6C; // JMP ($30FF)
        memory[0x1001] = 0xFF;
        memory[0x1002] = 0x30;
        memory[0x30FF] = 0x34; // target low byte
        memory[0x3000] = 0x12; // target high byte - real hardware wraps within the page...
        memory[0x3100] = 0x99; // ...rather than reading here, which a naive ReadWord would
        memory[0x1234] = 0x07;

        var result = ResolveEffectiveAddress(memory, x: 0x00, y: 0x00, address: 0x1000);

        await Assert.That(result).IsEqualTo(((ushort)0x1234, (byte)0x07));
    }

    [Test]
    public async Task TryGetEffectiveAddressReturnsNullWithoutRegisterCallbacks()
    {
        var memory = new byte[0x10000];
        memory[0x1000] = 0xB5; // LDA $10,X
        memory[0x1001] = 0x10;

        var memoryCallbacks = new DebuggerMemoryCallbacks(
            address => memory[address],
            (address, value) => memory[address] = value);
        var instruction = Mos6502Chip.DisassembleInstruction(0x1000, memoryCallbacks.Read, []);
        var disassembler = new Mos6502Disassembler(memoryCallbacks, []);

        await Assert.That(disassembler.TryGetEffectiveAddress(instruction)).IsNull();
    }

    [Test]
    public async Task TryGetEffectiveAddressReturnsNullForNonIndexedAddressing()
    {
        var memory = new byte[0x10000];
        memory[0x1000] = 0xAD; // LDA $1234 (plain absolute - the operand already is the address)
        memory[0x1001] = 0x34;
        memory[0x1002] = 0x12;

        var result = ResolveEffectiveAddress(memory, x: 0x05, y: 0x10, address: 0x1000);

        await Assert.That(result).IsNull();
    }

    private static (ushort Address, byte Value)? ResolveEffectiveAddress(byte[] memory, byte x, byte y, ushort address)
    {
        var memoryCallbacks = new DebuggerMemoryCallbacks(
            addr => memory[addr],
            (addr, value) => memory[addr] = value);

        var instruction = Mos6502Chip.DisassembleInstruction(address, memoryCallbacks.Read, []);

        var disassembler = new Mos6502Disassembler(
            memoryCallbacks,
            [],
            registerCallbacks: new Mos6502RegisterCallbacks(() => x, () => y));

        return disassembler.TryGetEffectiveAddress(instruction);
    }

    [Test]
    public async Task WritesRedisassembleCodeCopiedIntoRamAfterFirstDisassembly()
    {
        var memory = new byte[0x10000];

        // RESET: JSR $0090, then JMP-to-self. $0090 is still zero-filled
        // when disassembly starts, as RAM is when a ROM calls a routine it
        // copies into zero page later.
        memory[0xF000] = 0x20; memory[0xF001] = 0x90; memory[0xF002] = 0x00;
        memory[0xF003] = 0x4C; memory[0xF004] = 0x03; memory[0xF005] = 0xF0;
        memory[0xFFFC] = 0x00; memory[0xFFFD] = 0xF0;

        var disassembler = new Mos6502Disassembler(
            new DebuggerMemoryCallbacks(address => memory[address], (address, value) => memory[address] = value),
            [],
            hasNmi: false,
            hasIrq: false);

        disassembler.Reset();

        await Assert.That(disassembler.Cache[0x0090].Instruction!.Value.Mnemonic).IsEqualTo("BRK");
        // BRK doesn't fall through into the following zero bytes.
        await Assert.That(disassembler.Cache[0x0091].Instruction).IsNull();

        // Code copied in later: LDA #$05 / RTS.
        memory[0x0090] = 0xA9; memory[0x0091] = 0x05; memory[0x0092] = 0x60;

        // The write itself, then the read-back a cycle later.
        disassembler.OnDataWritten(0x0090);
        disassembler.OnDataWritten(0x0091);
        disassembler.OnDataWritten(0x0092);
        disassembler.ReseedInvalidated();

        await Assert.That(disassembler.Cache[0x0090].Instruction!.Value.Disassembly).IsEqualTo("LDA #$05");
        await Assert.That(disassembler.Cache[0x0090].Label).IsEqualTo("Subroutine");
        await Assert.That(disassembler.Cache[0x0092].Instruction!.Value.Mnemonic).IsEqualTo("RTS");
    }

    [Test]
    public async Task CanDisassembleSimpleInstructions()
    {
        var bytes = DasmHelper.Assemble(@"
        processor 6502

        org $F000

Start   nop
        jmp Start

        org $FFFC
        .word Start ; reset vector
        .word Start ; interrupt vector");

        var memoryCallbacks = new DebuggerMemoryCallbacks(
            address => bytes[address - 0xF000],
            (address, value) => throw new NotSupportedException());

        var disassembler = new Mos6502Disassembler(memoryCallbacks, []);

        disassembler.Reset();

        for (var i = 0; i < disassembler.Cache.Length; i++)
        {
            ref readonly var entry = ref disassembler.Cache[i];

            var (label, instruction) = (entry.Label, entry.Instruction);

            switch (i)
            {
                case 0xF000:
                    await Assert.That(label).IsEqualTo("RESET, IRQ / BRK");
                    await Assert.That(instruction).IsNotNull();
                    break;

                case 0xF001:
                    await Assert.That(label).IsNull();
                    await Assert.That(instruction).IsNotNull();
                    break;

                default:
                    await Assert.That(label).IsNull();
                    await Assert.That(instruction).IsNull();
                    break;
            }
        }
    }

    [Test]
    public async Task CanDisassembleSubroutine()
    {
        var bytes = DasmHelper.Assemble(@"
        processor 6502

        org $F000

Start
        jsr MySubroutine
        jmp Start

MySubroutine
        lda #$FF
        rts

        org $FFFC
        .word Start ; reset vector
        .word Start ; interrupt vector");

        var memoryCallbacks = new DebuggerMemoryCallbacks(
            address => bytes[address - 0xF000],
            (address, value) => throw new NotSupportedException());

        var disassembler = new Mos6502Disassembler(
            memoryCallbacks,
            []);

        disassembler.Reset();

        for (var i = 0; i < disassembler.Cache.Length; i++)
        {
            ref readonly var entry = ref disassembler.Cache[i];

            var (label, instruction) = (entry.Label, entry.Instruction);

            switch (i)
            {
                case 0xF000:
                    await Assert.That(label).IsEqualTo("RESET, IRQ / BRK");
                    await Assert.That(instruction).IsNotNull();
                    break;

                case 0xF003:
                    await Assert.That(label).IsNull();
                    await Assert.That(instruction).IsNotNull();
                    break;

                case 0xF006:
                    await Assert.That(label).IsEqualTo("Subroutine");
                    await Assert.That(instruction).IsNotNull();
                    break;

                case 0xF008:
                    await Assert.That(label).IsNull();
                    await Assert.That(instruction).IsNotNull();
                    break;

                default:
                    await Assert.That(label).IsNull();
                    await Assert.That(instruction).IsNull();
                    break;
            }
        }
    }
}

internal static class DasmHelper
{
    public static byte[] Assemble(string source)
    {
        var (fileNamePrefix, fileNameSuffix) = GetFileNamePrefixAndSuffix();

        var dasmPath = Path.GetFullPath($"../../../../../tools/dasm-2.20.14.1/{fileNamePrefix}-dasm{fileNameSuffix}");

        var sourcePath = Path.GetTempFileName();
        var destinationPath = Path.GetTempFileName();

        try
        {
            File.WriteAllText(sourcePath, source);

            var process = new Process();
            process.StartInfo.FileName = dasmPath;
            process.StartInfo.Arguments = $"\"{sourcePath}\" -o\"{destinationPath}\" -f3";
            process.StartInfo.RedirectStandardOutput = true;
            process.Start();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                var stdOutput = process.StandardOutput.ReadToEnd();
                throw new Exception(stdOutput);
            }

            return File.ReadAllBytes(destinationPath);
        }
        finally
        {
            File.Delete(destinationPath);
            File.Delete(sourcePath);
        }
    }

    private static (string prefix, string suffix) GetFileNamePrefixAndSuffix()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return ("win", ".exe");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return ("mac", "");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return ("linux", "");
        }
        else
        {
            throw new InvalidOperationException();
        }
    }
}
