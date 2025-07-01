using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Aemula.Debugging;
using Aemula.Emulation.Chips.Mos6502.Debugging;
using NUnit.Framework;

namespace Aemula.Tests.Emulation.Chips.Mos6502;

public class Mos6502DisassemblerTests
{
    [Test]
    public void CanDisassembleSimpleInstructions()
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

        var disassembler = new Mos6502Disassembler(
            memoryCallbacks,
            new Dictionary<ushort, string>());

        disassembler.Reset();

        for (var i = 0; i < disassembler.Cache.Length; i++)
        {
            ref readonly var entry = ref disassembler.Cache[i];

            switch (i)
            {
                case 0xF000:
                    Assert.AreEqual("RESET, IRQ / BRK", entry.Label);
                    Assert.NotNull(entry.Instruction);
                    break;

                case 0xF001:
                    Assert.AreEqual(null, entry.Label);
                    Assert.NotNull(entry.Instruction);
                    break;

                default:
                    Assert.AreEqual(null, entry.Label);
                    Assert.Null(entry.Instruction);
                    break;
            }
        }
    }

    [Test]
    public void CanDisassembleSubroutine()
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

            switch (i)
            {
                case 0xF000:
                    Assert.AreEqual("RESET, IRQ / BRK", entry.Label);
                    Assert.NotNull(entry.Instruction);
                    break;

                case 0xF003:
                    Assert.AreEqual(null, entry.Label);
                    Assert.NotNull(entry.Instruction);
                    break;

                case 0xF006:
                    Assert.AreEqual("Subroutine", entry.Label);
                    Assert.NotNull(entry.Instruction);
                    break;

                case 0xF008:
                    Assert.AreEqual(null, entry.Label);
                    Assert.NotNull(entry.Instruction);
                    break;

                default:
                    Assert.AreEqual(null, entry.Label);
                    Assert.Null(entry.Instruction);
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
            return ("osx", "");
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
