using System;
using System.IO;
using System.Threading.Tasks;
using Aemula.Emulation.Systems;
using Aemula.Emulation.Systems.Atari2600;

namespace Aemula.Tests.Emulation.Systems.Atari2600;

// Regression test for a real crash (System.IndexOutOfRangeException in
// Mos6532Chip.ReadByteDebug - see Atari2600SystemTests.
// ReadByteDebugMasksAddressToRiotsSevenAddressPins for the isolated repro
// of that same bug and the actual fix, which lives in
// Atari2600System.ReadByteDebug). The real-world repro (this session's
// actual crash) was live CPU execution reaching such an address at runtime
// via Atari2600Debugger's disassembler (Debugger.TickSystem ->
// OnAddressExecuting); this test instead forces the debugger's own *eager*
// disassembly walk (which follows every reachable jump/branch target from
// the reset vector - see Disassembler.DisassembleAddresses) into that same
// address range with a direct JMP, since that's deterministic at
// cartridge-insert time and doesn't depend on any particular real ROM's
// runtime control flow, while exercising the exact same
// Atari2600System.ReadByteDebug -> Mos6532Chip.ReadByteDebug path the
// live-execution crash went through.
public class Atari2600DebuggerTests
{
    [Test]
    public void LoadingCartridgeThatJumpsIntoRiotAddressRangeDoesNotThrow()
    {
        var rom = new byte[4096];

        // $1000: JMP $00FF - $00FF matches Atari2600System.ReadByteDebug's
        // RIOT-select mask ((address & 0x1280) == 0x80), the exact address
        // from the real crash report, but is well past Mos6532Chip's 128
        // bytes of RAM.
        rom[0] = 0x4C;
        rom[1] = 0xFF;
        rom[2] = 0x00;

        // Reset vector -> $1000.
        rom[0xFFC] = 0x00;
        rom[0xFFD] = 0x10;

        var path = Path.Combine(Path.GetTempPath(), $"aemula-atari2600-test-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, rom);

        try
        {
            var system = new Atari2600System();

            // Created before the cartridge goes in: the debugger subscribes to
            // System.MediaChanged in its base constructor, and inserting the
            // cartridge fires it, triggering the disassembler's eager walk
            // (Disassembler.Reset) this test relies on.
            system.CreateDebugger();

            system.InsertMedia("cartridge", MediaImage.FromFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ExecutionCyclesAreMeasuredInCpuCyclesNotColorClocks()
    {
        var rom = new byte[4096];

        // $1000: LDA #$01 (2 cycles); STA $80 (3); NOP (2); JMP $1000 (3).
        byte[] program = [0xA9, 0x01, 0x85, 0x80, 0xEA, 0x4C, 0x00, 0x10];
        program.CopyTo(rom, 0);
        rom[0xFFC] = 0x00;
        rom[0xFFD] = 0x10;

        var path = Path.Combine(Path.GetTempPath(), $"aemula-atari2600-test-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, rom);

        try
        {
            var system = new Atari2600System();
            var debugger = system.CreateDebugger();
            system.InsertMedia("cartridge", MediaImage.FromFile(path));

            debugger.Stopped = false;
            debugger.ActiveStepModeIndex = -1;
            debugger.RunForDuration(TimeSpan.FromMilliseconds(1));

            await Assert.That(debugger.LastExecutionCycles[0x1000]).IsEqualTo(2);
            await Assert.That(debugger.LastExecutionCycles[0x1002]).IsEqualTo(3);
            await Assert.That(debugger.LastExecutionCycles[0x1004]).IsEqualTo(2);
            await Assert.That(debugger.LastExecutionCycles[0x1005]).IsEqualTo(3);

            // Sync is held high across several color clocks per CPU cycle;
            // each pass round the loop must still count as one execution.
            var loopCount = debugger.Disassembler.ExecutionCounts[0x1000];
            await Assert.That(loopCount).IsGreaterThan(1);
            await Assert.That(debugger.Disassembler.ExecutionCounts[0x1002]).IsBetween(loopCount - 1, loopCount);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
