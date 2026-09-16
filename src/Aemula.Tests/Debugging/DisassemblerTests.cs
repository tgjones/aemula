using System;
using System.Threading.Tasks;
using Aemula.Debugging;
using Aemula.Emulation.Chips.Mos6502.Debugging;

namespace Aemula.Tests.Debugging;

// Exercises the base Disassembler class's chip-agnostic ExecutionCounts
// bookkeeping (used by DisassemblyWindow's execution-count heatmap) through
// Mos6502Disassembler, since Disassembler itself is abstract - none of these
// assertions depend on anything 6502-specific.
public class DisassemblerTests
{
    [Test]
    public async Task ExecutionCountsIncrementUnconditionallyOnEveryCall()
    {
        var memory = new byte[0x10000];

        var disassembler = new Mos6502Disassembler(
            new DebuggerMemoryCallbacks(
                address => memory[address],
                (address, value) => memory[address] = value),
            []);

        disassembler.OnAddressExecuting(0x1000);
        disassembler.OnAddressExecuting(0x1000);
        disassembler.OnAddressExecuting(0x1000);
        disassembler.OnAddressExecuting(0x2000);

        // Cache only records the *first* disassembly of an address (see
        // OnAddressExecuting's early return), but ExecutionCounts must keep
        // incrementing on every subsequent call to the same address, or the
        // heatmap would be indistinguishable from a "seen at least once" flag.
        await Assert.That(disassembler.ExecutionCounts[0x1000]).IsEqualTo(3);
        await Assert.That(disassembler.ExecutionCounts[0x2000]).IsEqualTo(1);
        await Assert.That(disassembler.ExecutionCounts[0x3000]).IsEqualTo(0);
    }

    [Test]
    public async Task ResetClearsExecutionCounts()
    {
        var memory = new byte[0x10000];

        var disassembler = new Mos6502Disassembler(
            new DebuggerMemoryCallbacks(
                address => memory[address],
                (address, value) => memory[address] = value),
            []);

        disassembler.OnAddressExecuting(0x1000);
        disassembler.OnAddressExecuting(0x1000);

        // A media change (new cartridge) re-walks the code map via Reset() -
        // stale execution counts from the previous program shouldn't linger
        // and paint the new listing as already hot.
        disassembler.Reset();

        await Assert.That(disassembler.ExecutionCounts[0x1000]).IsEqualTo(0);
    }
}
