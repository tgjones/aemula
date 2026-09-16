using System;
using System.Threading.Tasks;
using Aemula.Debugging;
using Aemula.Emulation.Chips.Mos6502.Debugging;

namespace Aemula.Tests.Debugging;

// Exercises Debugger.TotalTicks / LastExecutionCycles bookkeeping (used by
// DisassemblyWindow's cycle-count annotation) through a minimal fake system
// and debugger - none of this logic is chip-specific, so a real chip would
// only add noise.
public class DebuggerTests
{
    [Test]
    public async Task LastExecutionCyclesRecordsGapBetweenConsecutiveFetches()
    {
        var debugger = CreateDebugger();

        debugger.Fetch(0x1000);
        debugger.RunForDuration(TimeSpan.FromSeconds(0.4)); // 4 ticks at 10 Hz
        debugger.Fetch(0x1004);

        // The gap since the previous fetch is charged to *that* fetch's
        // address, not the new one - 0x1000 took 4 ticks to reach 0x1004.
        await Assert.That(debugger.LastExecutionCycles[0x1000]).IsEqualTo(4);
        // 0x1004 hasn't completed an execution yet, so it stays at the
        // "no measurement" zero DisassemblyWindow treats as blank.
        await Assert.That(debugger.LastExecutionCycles[0x1004]).IsEqualTo(0);

        debugger.RunForDuration(TimeSpan.FromSeconds(0.7)); // 7 more ticks
        debugger.Fetch(0x1000);

        // Revisiting 0x1000 overwrites its old measurement with this run's.
        await Assert.That(debugger.LastExecutionCycles[0x1004]).IsEqualTo(7);
    }

    [Test]
    public async Task TotalTicksIncrementsOncePerRunForDurationIteration()
    {
        var debugger = CreateDebugger();

        debugger.RunForDuration(TimeSpan.FromSeconds(1)); // 10 ticks at 10 Hz

        await Assert.That(debugger.TotalTicks).IsEqualTo(10);
    }

    [Test]
    public async Task MediaChangeClearsLastExecutionCyclesAndPreviousFetchState()
    {
        var system = new FakeSystem();
        var debugger = new FakeDebugger(system) { Stopped = false, ActiveStepModeIndex = -1 };

        debugger.Fetch(0x1000);
        debugger.RunForDuration(TimeSpan.FromSeconds(0.4));
        debugger.Fetch(0x1004);

        await Assert.That(debugger.LastExecutionCycles[0x1000]).IsEqualTo(4);

        system.SimulateMediaChanged();

        await Assert.That(debugger.LastExecutionCycles[0x1000]).IsEqualTo(0);

        // The pre-media-change fetch shouldn't leak into a post-change
        // measurement either - the very next fetch has no previous fetch to
        // be charged against.
        debugger.RunForDuration(TimeSpan.FromSeconds(0.4));
        debugger.Fetch(0x2000);

        await Assert.That(debugger.LastExecutionCycles[0x1004]).IsEqualTo(0);
    }

    private static FakeDebugger CreateDebugger()
    {
        var system = new FakeSystem();
        return new FakeDebugger(system) { Stopped = false, ActiveStepModeIndex = -1 };
    }

    private sealed class FakeSystem : EmulatedSystem
    {
        public override ulong CyclesPerSecond => 10;

        public override void Tick()
        {
        }

        public void SimulateMediaChanged() => RaiseMediaChanged();
    }

    private sealed class FakeDebugger(EmulatedSystem system)
        : Debugger(system, new DebuggerMemoryCallbacks(_ => 0, (_, _) => { }))
    {
        protected override Disassembler CreateDisassembler() => new Mos6502Disassembler(MemoryCallbacks, []);

        public void Fetch(ushort address) => OnAddressExecuting(address);
    }
}
