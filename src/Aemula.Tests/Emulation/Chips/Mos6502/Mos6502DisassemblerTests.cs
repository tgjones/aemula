using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Aemula.Debugging;
using Aemula.Emulation.Chips.Mos6502.Debugging;

namespace Aemula.Tests.Emulation.Chips.Mos6502;

public class Mos6502DisassemblerTests
{
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
            new Dictionary<ushort, string>());

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
