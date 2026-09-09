using System.Threading.Tasks;
using Aemula.Emulation.Chips.Z80;

namespace Aemula.Tests.Emulation.Chips.Z80;

// The /WAIT-driven wait-state (Tw) mechanism a peripheral or memory controller
// uses to stall the Z80 mid-machine-cycle, plus the wait state the Z80 wires
// into every I/O cycle on its own. Exercises Z80Chip directly by driving raw
// CLK edges and machine cycles - no opcodes are needed - the same way the
// Intel 8080's Intel8080ChipWaitStateTests does.
//
// One T-state is one full CLK period: chip.Clk = true; chip.Clk = false;
public class Z80ChipWaitStateTests
{
    [Test]
    public async Task WaitDefaultsDeassertedAndAnUntouchedPinNeverStalls()
    {
        var cpu = new Z80Chip();

        await Assert.That(cpu.Wait).IsTrue();

        // The power-on M1 opcode fetch: with /WAIT left alone it walks
        // T1 -> T2 -> T3 -> T4 with no Tw inserted anywhere.

        // First CLK rising edge applies the constructor's staged "start fetching"
        // transition, landing on T1 itself.
        cpu.Clk = true;
        cpu.Clk = false;
        await Assert.That(cpu.CurrentMachineCycle).IsEqualTo(Z80Chip.MachineCycleType.OpcodeFetch);
        await Assert.That(cpu.CurrentState).IsEqualTo(Z80Chip.TState.T1);

        cpu.Clk = true;
        cpu.Clk = false;
        await Assert.That(cpu.CurrentState).IsEqualTo(Z80Chip.TState.T2);

        cpu.Clk = true;
        cpu.Clk = false;
        await Assert.That(cpu.CurrentState).IsEqualTo(Z80Chip.TState.T3);

        cpu.Clk = true;
        cpu.Clk = false;
        await Assert.That(cpu.CurrentState).IsEqualTo(Z80Chip.TState.T4);
    }

    [Test]
    public async Task LowWaitAcrossT2FallingEdgeInsertsTwRepeatsWhileHeldAndResumesAtT3()
    {
        var cpu = new Z80Chip();

        // T1.
        cpu.Clk = true;
        cpu.Clk = false;
        await Assert.That(cpu.CurrentState).IsEqualTo(Z80Chip.TState.T1);

        // Advance to T2, then assert /WAIT low before T2's falling edge so it is
        // the level the CPU latches there.
        cpu.Clk = true;
        await Assert.That(cpu.CurrentState).IsEqualTo(Z80Chip.TState.T2);
        cpu.Wait = false;
        cpu.Clk = false;

        // T2 -> Tw: /WAIT was low across T2's falling edge, so this rising edge
        // inserts Tw instead of advancing to T3.
        cpu.Clk = true;
        await Assert.That(cpu.CurrentState).IsEqualTo(Z80Chip.TState.Tw);
        cpu.Clk = false;

        // Tw -> Tw: still held low, stays parked.
        cpu.Clk = true;
        await Assert.That(cpu.CurrentState).IsEqualTo(Z80Chip.TState.Tw);
        cpu.Clk = false;

        // Tw -> Tw again.
        cpu.Clk = true;
        await Assert.That(cpu.CurrentState).IsEqualTo(Z80Chip.TState.Tw);

        // Release /WAIT before this Tw's falling edge; the CPU samples it high
        // there, so the next rising edge resumes at T3.
        cpu.Wait = true;
        cpu.Clk = false;

        cpu.Clk = true;
        await Assert.That(cpu.CurrentState).IsEqualTo(Z80Chip.TState.T3);
        cpu.Clk = false;
    }

    [Test]
    public async Task AddressAndDataBusStayValidAcrossTw()
    {
        var cpu = new Z80Chip
        {
            // Stand in for whatever memory drives onto the bus during the fetch.
            Data = 0xAB,
        };

        // T1: the address bus is loaded with PC, which is 0x0000 at power-on.
        cpu.Clk = true;
        cpu.Clk = false;
        var addressDuringFetch = cpu.Address;
        await Assert.That(addressDuringFetch).IsEqualTo((ushort)0x0000);

        // T2, with /WAIT pulled low so Tw states follow.
        cpu.Clk = true;
        cpu.Wait = false;
        cpu.Clk = false;

        // Across every inserted Tw the address and data buses must not move - a
        // real device relies on them staying valid for the whole stall.
        for (var i = 0; i < 3; i++)
        {
            cpu.Clk = true;
            await Assert.That(cpu.CurrentState).IsEqualTo(Z80Chip.TState.Tw);
            await Assert.That(cpu.Address).IsEqualTo(addressDuringFetch);
            await Assert.That(cpu.Data).IsEqualTo((byte)0xAB);
            cpu.Clk = false;
        }
    }

    [Test]
    public async Task IoMachineCycleAlwaysShowsExactlyOneBuiltInTwWithWaitUntouched()
    {
        var cpu = new Z80Chip();

        // Drive the state machine straight into an I/O read cycle. /WAIT is never
        // touched, so the only Tw that can appear is the one the Z80 wires into
        // every I/O cycle between T2 and T3.
        cpu.StageNextMachineCycleForTest(Z80Chip.MachineCycleType.IoRead);

        cpu.Clk = true;
        cpu.Clk = false;
        await Assert.That(cpu.CurrentMachineCycle).IsEqualTo(Z80Chip.MachineCycleType.IoRead);
        await Assert.That(cpu.CurrentState).IsEqualTo(Z80Chip.TState.T1);

        cpu.Clk = true;
        cpu.Clk = false;
        await Assert.That(cpu.CurrentState).IsEqualTo(Z80Chip.TState.T2);

        cpu.Clk = true;
        cpu.Clk = false;
        await Assert.That(cpu.CurrentState).IsEqualTo(Z80Chip.TState.Tw);
        await Assert.That(cpu.Wait).IsTrue();

        cpu.Clk = true;
        cpu.Clk = false;
        await Assert.That(cpu.CurrentState).IsEqualTo(Z80Chip.TState.T3);
    }
}
