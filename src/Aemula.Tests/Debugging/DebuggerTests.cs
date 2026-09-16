using System;
using System.Collections.Generic;
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

    [Test]
    public async Task RunToAddressStopsOnceAndDoesNotStopAgainOnceCleared()
    {
        var debugger = CreateDebugger();
        // A little "loop": 0x1000 -> 0x1002 -> 0x1004 -> back to 0x1000.
        debugger.ScriptedFetches.Enqueue(0x1000);
        debugger.ScriptedFetches.Enqueue(0x1002);
        debugger.ScriptedFetches.Enqueue(0x1004);
        debugger.ScriptedFetches.Enqueue(0x1000);

        debugger.RunToAddress(0x1002);
        debugger.RunForDuration(TimeSpan.FromSeconds(1)); // 10 ticks - plenty to reach it

        await Assert.That(debugger.Stopped).IsTrue();
        await Assert.That(debugger.LastPC).IsEqualTo((ushort)0x1002);

        // One-shot: resuming and coming back around to 0x1002 again (or
        // running past whatever's left of the script) shouldn't re-trigger
        // it, since RunForDuration clears it the moment it's hit.
        debugger.Stopped = false;
        debugger.RunForDuration(TimeSpan.FromSeconds(1));

        await Assert.That(debugger.Stopped).IsFalse();
    }

    [Test]
    public async Task RunToAddressDoesNotItselfResumeAStoppedDebugger()
    {
        var debugger = new FakeDebugger(new FakeSystem());

        debugger.RunToAddress(0x1002);

        // Debugger starts Stopped (see Debugger's constructor) - setting a
        // run-to-cursor target is not itself a "Continue".
        await Assert.That(debugger.Stopped).IsTrue();
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
        // One scripted fetch consumed per tick, mimicking a real chip
        // debugger's TickSystem override calling OnAddressExecuting on
        // sync - lets RunToAddress tests drive LastPC through a known
        // sequence without a real chip.
        public Queue<ushort> ScriptedFetches { get; } = new();

        protected override Disassembler CreateDisassembler() => new Mos6502Disassembler(MemoryCallbacks, []);

        public void Fetch(ushort address) => OnAddressExecuting(address);

        protected override void TickSystem()
        {
            base.TickSystem();

            if (ScriptedFetches.Count > 0)
            {
                OnAddressExecuting(ScriptedFetches.Dequeue());
            }
        }
    }
}
