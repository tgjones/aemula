using System.Threading.Tasks;
using Aemula.Emulation.Chips.Intel8080;

namespace Aemula.Tests.Emulation.Chips.Intel8080;

// The READY-driven wait-state (Tw) mechanism a real bus-arbitration circuit
// (Space Invaders' video scanner, here) relies on to stall the CPU
// mid-machine-cycle. Exercises Intel8080Chip directly, independent of
// SpaceInvadersSystem, the same way Intel8080ChipTests' CP/M conformance
// test does.
public class Intel8080ChipWaitStateTests
{
    [Test]
    public async Task ReadyDefaultsTrueSoUntouchedPinNeverStalls()
    {
        var cpu = new Intel8080Chip();

        await Assert.That(cpu.Ready).IsTrue();
    }

    [Test]
    public async Task LowReadyDuringT2InsertsTwAndAssertsWait()
    {
        var cpu = new Intel8080Chip
        {
            Ready = false,
        };

        // T-state 1 (T1): the CPU's very first Phi1 rising edge applies the
        // constructor's pending "start fetching" transition, landing on T1
        // itself rather than advancing out of it - READY isn't sampled
        // here.
        cpu.Phi1 = true;
        cpu.Phi1 = false;
        cpu.Phi2 = true;
        cpu.Phi2 = false;
        await Assert.That(cpu.CurrentState).IsEqualTo(Intel8080Chip.State.T1);

        // T-state 2 (T2): DBIn asserts on this state's Phi2 rising edge: a
        // real device would need to keep the bus valid from here through
        // however many Tw states follow.
        cpu.Phi1 = true;
        cpu.Phi1 = false;
        cpu.Phi2 = true;
        cpu.Phi2 = false;
        await Assert.That(cpu.CurrentState).IsEqualTo(Intel8080Chip.State.T2);
        await Assert.That(cpu.DBIn).IsTrue();
        await Assert.That(cpu.Wait).IsFalse();

        // T2 -> Tw: READY was low across T2's own Phi2, so this Phi1 rising
        // edge inserts Tw instead of advancing to T3.
        cpu.Phi1 = true;
        await Assert.That(cpu.CurrentState).IsEqualTo(Intel8080Chip.State.Tw);
        await Assert.That(cpu.Wait).IsTrue();
        cpu.Phi1 = false;
        cpu.Phi2 = true;
        cpu.Phi2 = false;

        // Tw -> Tw: still not ready, stays parked with WAIT asserted.
        cpu.Phi1 = true;
        await Assert.That(cpu.CurrentState).IsEqualTo(Intel8080Chip.State.Tw);
        await Assert.That(cpu.Wait).IsTrue();
        cpu.Phi1 = false;

        // DBIn must still read asserted throughout the wait - a real
        // device is expected to keep the data bus valid across Tw.
        await Assert.That(cpu.DBIn).IsTrue();

        cpu.Ready = true;
        cpu.Phi2 = true;
        cpu.Phi2 = false;

        // Tw -> T3: READY sampled high this Phi2, so the wait ends here.
        cpu.Phi1 = true;
        await Assert.That(cpu.CurrentState).IsEqualTo(Intel8080Chip.State.T3);
        await Assert.That(cpu.Wait).IsFalse();
        cpu.Phi1 = false;
    }
}
