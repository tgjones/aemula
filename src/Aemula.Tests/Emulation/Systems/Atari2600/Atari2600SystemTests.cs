using System;
using System.IO;
using System.Threading.Tasks;
using Aemula.Emulation.Systems.Atari2600;

namespace Aemula.Tests.Emulation.Systems.Atari2600;

// Basic non-video sanity, the same minimal-smoke-test role
// AppleIISystemTests plays: LoadProgram + ticking doesn't throw, and the
// CPU is actually fetching/executing cartridge code (not just sitting on
// its reset vector).
public class Atari2600SystemTests
{
    // Atari2600System.LoadProgram reads cartridge bytes from disk (unlike
    // AppleIISystem's LoadProgram("")), so every test here needs a real
    // temp file - a 2K cartridge whose reset vector points straight back at
    // an infinite JMP to itself, the smallest cartridge that lets the CPU
    // run without depending on any TIA/RIOT register behavior.
    private static byte[] BuildInfiniteLoopCartridge()
    {
        var rom = new byte[2048];

        // $1000: JMP $1000
        rom[0] = 0x4C;
        rom[1] = 0x00;
        rom[2] = 0x10;

        // Reset vector ($FFFC/$FFFD) -> $1000. Cartridge2K mirrors its 2K
        // image across the whole 4K cartridge-selected window, so $FFFC's
        // masked-down address ($1FFC) lands on the same $07FC/$07FD bytes
        // as $1000's own mirror at $1800 would.
        rom[0x7FC] = 0x00;
        rom[0x7FD] = 0x10;

        return rom;
    }

    // The standard "CLEAN_START" pattern virtually every real cartridge
    // opens with (SEI/CLD, set up the stack, zero RIOT's RAM), followed by
    // a JSR/RTS - exercises the CPU's stack (RIOT RAM at $80-$FF, via SP)
    // and subroutine call/return, neither of which the plain infinite-loop
    // cartridge above touches at all.
    private static byte[] BuildStackAndSubroutineCartridge()
    {
        var rom = new byte[2048];

        var program = new byte[]
        {
            0x78,             // $1000: SEI
            0xD8,             // $1001: CLD
            0xA2, 0xFF,       // $1002: LDX #$FF
            0x9A,             // $1004: TXS
            0xA9, 0x00,       // $1005: LDA #$00
            0x95, 0x00,       // $1007: CLEAR: STA $00,X
            0xCA,             // $1009: DEX
            0xD0, 0xFB,       // $100A: BNE CLEAR
            0x20, 0x12, 0x10, // $100C: JSR $1012 (SUB)
            0x4C, 0x0F, 0x10, // $100F: LOOP: JMP $100F
            0x60,             // $1012: SUB: RTS
        };

        Array.Copy(program, rom, program.Length);

        // Reset vector -> $1000.
        rom[0x7FC] = 0x00;
        rom[0x7FD] = 0x10;

        return rom;
    }

    private static string WriteCartridgeToTempFile(byte[] rom)
    {
        var path = Path.Combine(Path.GetTempPath(), $"aemula-atari2600-test-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, rom);
        return path;
    }

    [Test]
    public async Task LoadProgramAndTickDoesNotThrow()
    {
        var system = new Atari2600System();
        var path = WriteCartridgeToTempFile(BuildInfiniteLoopCartridge());

        try
        {
            system.LoadProgram(path);
            system.Reset();

            for (var i = 0; i < 200_000; i++)
            {
                system.Tick();
            }

            // Confirms the CPU actually settled into the cartridge's tiny
            // JMP-to-self loop (the address bus cycles through
            // $1000/$1001/$1002 as it fetches the 3-byte JMP instruction,
            // landing back on $1000 every 3rd cycle) rather than, say, chasing
            // garbage off into unmapped address space - not just "didn't throw".
            await Assert.That(system.Cpu.Address).IsBetween((ushort)0x1000, (ushort)0x1002);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task CpuFetchesAndLoopsCartridgeCode()
    {
        var system = new Atari2600System();
        var path = WriteCartridgeToTempFile(BuildInfiniteLoopCartridge());

        try
        {
            system.LoadProgram(path);
            system.Reset();

            var reachedLoop = false;

            for (var i = 0; i < 200_000; i++)
            {
                system.Tick();

                if (system.Cpu.Sync && system.Cpu.Address == 0x1000)
                {
                    reachedLoop = true;
                    break;
                }
            }

            await Assert.That(reachedLoop).IsTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Regression test for a real crash (System.IndexOutOfRangeException in
    // Mos6532Chip.ReadByteDebug - see Atari2600DebuggerTests for the
    // end-to-end repro via the debugger's disassembler): ReadByteDebug's
    // RIOT-select mask ((address & 0x1280) == 0x80) only pins bits 7/9/12,
    // leaving the low 7 bits free, so addresses like $00FF (matching the
    // select mask but past the chip's 128 bytes of RAM) reached
    // Mos6532Chip.ReadByteDebug unmasked. Real RIOT only has 7 address pins
    // (A0..A6) wired up - the same masking DoAddressDecode already applies
    // to _riot.A during normal ticking - so the fix is Atari2600System
    // applying that same address-pin masking on the debug path too, not
    // Mos6532Chip guarding against an out-of-range address it should never
    // receive.
    [Test]
    public async Task ReadByteDebugMasksAddressToRiotsSevenAddressPins()
    {
        var system = new Atari2600System();

        var expected = system.ReadByteDebug(0x0000);

        await Assert.That(system.ReadByteDebug(0x0080)).IsEqualTo(expected);
        await Assert.That(system.ReadByteDebug(0x0180)).IsEqualTo(expected);
        await Assert.That(system.ReadByteDebug(0xFF80)).IsEqualTo(expected);

        // The exact address from the real crash report.
        await Assert.That(system.ReadByteDebug(0x00FF)).IsEqualTo(system.ReadByteDebug(0x007F));
    }

    [Test]
    public async Task InitializesStackAndCallsSubroutineWithoutThrowing()
    {
        var system = new Atari2600System();
        var path = WriteCartridgeToTempFile(BuildStackAndSubroutineCartridge());

        try
        {
            system.LoadProgram(path);
            system.Reset();

            for (var i = 0; i < 200_000; i++)
            {
                system.Tick();
            }

            // Confirms the CPU made it all the way through the RAM-clear
            // loop and the JSR/RTS, settling into the tail JMP-to-self loop -
            // not stuck mid-subroutine or off in unmapped address space.
            await Assert.That(system.Cpu.Address).IsBetween((ushort)0x100F, (ushort)0x1011);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
