using System.Threading.Tasks;
using Aemula.Emulation.Chips.Z80;

namespace Aemula.Tests.Emulation.Chips.Z80;

// Hand-written checks of the /NMI, /INT (modes 0-2), HALT-wake and /BUSRQ state
// machine, driving raw CLK edges against a 64K RAM array serviced off the
// control pins - the same style as Z80ChipWaitStateTests. No ROM covers these.
//
// T-state expectations come from the Zilog Z80 CPU User Manual (UM0080)
// interrupt-timing diagrams: NMI is 11 T-states (a 5-T M1-like acknowledge plus
// two 3-T stack writes); INT in mode 1 and mode 0 (with 0xFF -> RST 38h) is
// 13 T; INT in mode 2 is 19 T (the acknowledge, the push, then a two-byte
// vector-table read).
//
// One T-state is one full CLK period: chip.Clk = true; chip.Clk = false.
public class Z80ChipInterruptTests
{
    private sealed class Bus
    {
        public readonly byte[] Ram = new byte[0x10000];

        // The byte an interrupting device jams on the data bus during an /INT
        // acknowledge (/M1 low with /IORQ low). 0xFF is the classic floating-bus
        // value that mode 0 decodes as RST 38h.
        public byte AckByte = 0xFF;

        public void Tick(Z80Chip cpu)
        {
            cpu.Clk = true;
            cpu.Clk = false;

            if (!cpu.MReq && !cpu.Rd)
            {
                cpu.Data = Ram[cpu.Address];
            }

            if (!cpu.MReq && !cpu.Wr)
            {
                Ram[cpu.Address] = cpu.Data;
            }

            if (!cpu.IoRq && !cpu.Rd)
            {
                cpu.Data = (byte)(cpu.Address >> 8);
            }

            if (!cpu.M1 && !cpu.IoRq)
            {
                cpu.Data = AckByte;
            }
        }
    }

    // A fresh chip parked on its power-on instruction boundary (PC = 0, nothing
    // fetched yet), with the given bytes loaded from 0x0000.
    private static (Z80Chip Cpu, Bus Bus) Boot(params byte[] program)
    {
        var bus = new Bus();
        for (var i = 0; i < program.Length; i++)
        {
            bus.Ram[i] = program[i];
        }

        var cpu = new Z80Chip();
        cpu.Flags.SetFromByte(cpu.AF.F);
        return (cpu, bus);
    }

    // Runs from an instruction boundary through to the next one, returning the
    // number of T-states that took (the final T-state, on which the next
    // boundary is reported, is counted).
    private static int RunInstruction(Z80Chip cpu, Bus bus, int guard = 400)
    {
        var t = 0;
        bus.Tick(cpu);
        t++;

        while (!cpu.AtInstructionBoundary && t < guard)
        {
            bus.Tick(cpu);
            t++;
        }

        return t;
    }

    // --- /NMI -----------------------------------------------------------------

    [Test]
    public async Task NmiEdgeIsLatchedAndVectorsTo0x0066InElevenTStates()
    {
        // NOP-filled memory; RETN at the NMI vector.
        var (cpu, bus) = Boot();
        bus.Ram[0x0066] = 0xED;
        bus.Ram[0x0067] = 0x45; // RETN

        cpu.SP.Value = 0xFFFF;
        cpu.IFF1 = true;
        cpu.IFF2 = false;

        // A high->low edge on /NMI, latched the instant it happens.
        cpu.Nmi = false;

        var tStates = RunInstruction(cpu, bus);

        await Assert.That(tStates).IsEqualTo(11);
        await Assert.That(cpu.PC.Value).IsEqualTo((ushort)0x0066);
        await Assert.That(cpu.WZ.Value).IsEqualTo((ushort)0x0066);

        // IFF1 is saved into IFF2 and then cleared.
        await Assert.That(cpu.IFF2).IsTrue().Because("IFF1 (was set) copied into IFF2");
        await Assert.That(cpu.IFF1).IsFalse();

        // The return address (PC before the NMI - 0x0000 here) was pushed, SP
        // down by two.
        await Assert.That(cpu.SP.Value).IsEqualTo((ushort)0xFFFD);
        await Assert.That(bus.Ram[0xFFFE]).IsEqualTo((byte)0x00).Because("PC high");
        await Assert.That(bus.Ram[0xFFFD]).IsEqualTo((byte)0x00).Because("PC low");

        // RETN restores IFF1 from IFF2 and pops the return address.
        RunInstruction(cpu, bus);
        await Assert.That(cpu.PC.Value).IsEqualTo((ushort)0x0000);
        await Assert.That(cpu.IFF1).IsTrue().Because("RETN copies IFF2 -> IFF1");
        await Assert.That(cpu.SP.Value).IsEqualTo((ushort)0xFFFF);
    }

    [Test]
    public async Task NmiHeldLowDoesNotRetriggerWithoutAFreshEdge()
    {
        var (cpu, bus) = Boot();
        bus.Ram[0x0066] = 0xED;
        bus.Ram[0x0067] = 0x45; // RETN
        cpu.SP.Value = 0xFFFF;

        cpu.Nmi = false;            // edge 1
        RunInstruction(cpu, bus);   // NMI taken
        await Assert.That(cpu.PC.Value).IsEqualTo((ushort)0x0066);

        RunInstruction(cpu, bus);   // RETN, back to 0x0000
        await Assert.That(cpu.PC.Value).IsEqualTo((ushort)0x0000);

        // /NMI is still low, but with no new falling edge it must not fire again.
        RunInstruction(cpu, bus);   // a plain NOP
        await Assert.That(cpu.PC.Value).IsEqualTo((ushort)0x0001);

        // A fresh high->low edge re-arms it.
        cpu.Nmi = true;
        cpu.Nmi = false;
        RunInstruction(cpu, bus);
        await Assert.That(cpu.PC.Value).IsEqualTo((ushort)0x0066);
    }

    // --- EI / DI shadow -----------------------------------------------------

    [Test]
    public async Task IntIsNotTakenUntilAfterTheInstructionFollowingEi()
    {
        // EI ; NOP ; NOP ; ...  with /INT asserted the whole time, mode 1.
        var (cpu, bus) = Boot(0xFB, 0x00, 0x00, 0x00);
        cpu.SP.Value = 0xFFFF;
        cpu.IM = 1;
        cpu.Int = false;

        RunInstruction(cpu, bus); // EI
        await Assert.That(cpu.PC.Value).IsEqualTo((ushort)0x0001);
        await Assert.That(cpu.IFF1).IsTrue();

        RunInstruction(cpu, bus); // the shadowed instruction - INT still not taken
        await Assert.That(cpu.PC.Value).IsEqualTo((ushort)0x0002);

        var tStates = RunInstruction(cpu, bus); // now the INT is accepted
        await Assert.That(cpu.PC.Value).IsEqualTo((ushort)0x0038);
        await Assert.That(tStates).IsEqualTo(13);
        await Assert.That(cpu.IFF1).IsFalse();
        await Assert.That(cpu.IFF2).IsFalse();
    }

    [Test]
    public async Task DiLeavesNoWindowInWhichIntIsTaken()
    {
        // EI ; DI ; NOP ; NOP  - the DI cancels the enable inside the EI shadow,
        // so /INT (asserted throughout, mode 1) is never accepted.
        var (cpu, bus) = Boot(0xFB, 0xF3, 0x00, 0x00);
        cpu.SP.Value = 0xFFFF;
        cpu.IM = 1;
        cpu.Int = false;

        RunInstruction(cpu, bus); // EI
        RunInstruction(cpu, bus); // DI
        RunInstruction(cpu, bus); // NOP
        RunInstruction(cpu, bus); // NOP

        await Assert.That(cpu.PC.Value).IsEqualTo((ushort)0x0004);
        await Assert.That(cpu.IFF1).IsFalse();
    }

    [Test]
    public async Task ConsecutiveEiDoesNotStackTheDelay()
    {
        // EI ; EI ; NOP ; NOP - still exactly one instruction of shadow after
        // the last EI, not two.
        var (cpu, bus) = Boot(0xFB, 0xFB, 0x00, 0x00);
        cpu.SP.Value = 0xFFFF;
        cpu.IM = 1;
        cpu.Int = false;

        RunInstruction(cpu, bus); // EI
        RunInstruction(cpu, bus); // EI
        RunInstruction(cpu, bus); // NOP (the one shadowed instruction)
        await Assert.That(cpu.PC.Value).IsEqualTo((ushort)0x0003);

        RunInstruction(cpu, bus); // INT accepted here
        await Assert.That(cpu.PC.Value).IsEqualTo((ushort)0x0038);
        await Assert.That(bus.Ram[0xFFFD]).IsEqualTo((byte)0x03).Because("return address low");
    }

    // --- /INT modes -------------------------------------------------------

    [Test]
    public async Task IntMode1ExecutesRst38InThirteenTStates()
    {
        var (cpu, bus) = Boot();
        cpu.SP.Value = 0xFFFF;
        cpu.IM = 1;
        cpu.IFF1 = true;
        cpu.IFF2 = true;
        cpu.Int = false;

        var tStates = RunInstruction(cpu, bus);

        await Assert.That(tStates).IsEqualTo(13);
        await Assert.That(cpu.PC.Value).IsEqualTo((ushort)0x0038);
        await Assert.That(cpu.WZ.Value).IsEqualTo((ushort)0x0038);
        await Assert.That(cpu.IFF1).IsFalse();
        await Assert.That(cpu.IFF2).IsFalse();
        await Assert.That(cpu.SP.Value).IsEqualTo((ushort)0xFFFD);
        await Assert.That(bus.Ram[0xFFFD]).IsEqualTo((byte)0x00).Because("PC low pushed");
        await Assert.That(bus.Ram[0xFFFE]).IsEqualTo((byte)0x00).Because("PC high pushed");
    }

    [Test]
    public async Task IntMode2ReadsTheVectorTableInNineteenTStates()
    {
        var (cpu, bus) = Boot();
        cpu.SP.Value = 0xFFFF;
        cpu.IM = 2;
        cpu.I = 0x12;
        cpu.IFF1 = true;
        cpu.IFF2 = true;
        cpu.Int = false;

        // Device jams vector 0x34 -> pointer 0x1234; handler address 0x5678
        // stored little-endian there.
        bus.AckByte = 0x34;
        bus.Ram[0x1234] = 0x78;
        bus.Ram[0x1235] = 0x56;

        var tStates = RunInstruction(cpu, bus);

        await Assert.That(tStates).IsEqualTo(19);
        await Assert.That(cpu.PC.Value).IsEqualTo((ushort)0x5678);
        await Assert.That(cpu.WZ.Value).IsEqualTo((ushort)0x5678).Because("WZ = handler address");
        await Assert.That(cpu.IFF1).IsFalse();
        await Assert.That(cpu.IFF2).IsFalse();
        await Assert.That(bus.Ram[0xFFFD]).IsEqualTo((byte)0x00).Because("return address low");
    }

    [Test]
    public async Task IntMode0WithFloatingBusExecutesRst38()
    {
        var (cpu, bus) = Boot();
        cpu.SP.Value = 0xFFFF;
        cpu.IM = 0;
        cpu.IFF1 = true;
        cpu.Int = false;
        bus.AckByte = 0xFF; // -> RST 38h

        var tStates = RunInstruction(cpu, bus);

        await Assert.That(tStates).IsEqualTo(13);
        await Assert.That(cpu.PC.Value).IsEqualTo((ushort)0x0038);
    }

    [Test]
    public async Task InterruptAcknowledgeCycleShowsM1AndIorqLowWithTwoBuiltInWaitStates()
    {
        var (cpu, bus) = Boot();
        cpu.SP.Value = 0xFFFF;
        cpu.IM = 1;
        cpu.IFF1 = true;
        cpu.Int = false;

        // Tick 1: T1 of the acknowledge - /M1 low, /IORQ not yet.
        bus.Tick(cpu);
        await Assert.That(cpu.CurrentMachineCycle).IsEqualTo(Z80Chip.MachineCycleType.InterruptAck);
        await Assert.That(cpu.CurrentState).IsEqualTo(Z80Chip.TState.T1);
        await Assert.That(cpu.M1).IsFalse();

        // Tick 2: T2 - /IORQ falls (with /M1 still low: the acknowledge strobe).
        bus.Tick(cpu);
        await Assert.That(cpu.CurrentState).IsEqualTo(Z80Chip.TState.T2);
        await Assert.That(cpu.M1).IsFalse();
        await Assert.That(cpu.IoRq).IsFalse();

        // Ticks 3 and 4: the two automatic wait states.
        bus.Tick(cpu);
        await Assert.That(cpu.CurrentState).IsEqualTo(Z80Chip.TState.Tw);
        bus.Tick(cpu);
        await Assert.That(cpu.CurrentState).IsEqualTo(Z80Chip.TState.Tw);

        // Ticks 5 and 6: T3 (strobes released) then T4.
        bus.Tick(cpu);
        await Assert.That(cpu.CurrentState).IsEqualTo(Z80Chip.TState.T3);
        await Assert.That(cpu.IoRq).IsTrue();
        bus.Tick(cpu);
        await Assert.That(cpu.CurrentState).IsEqualTo(Z80Chip.TState.T4);
    }

    // --- HALT wake ------------------------------------------------------

    [Test]
    public async Task HaltEmitsFourTNopM1sAndAnInterruptWakesItPastTheHaltByte()
    {
        var (cpu, bus) = Boot(0x76); // HALT at 0x0000
        cpu.SP.Value = 0xFFFF;

        RunInstruction(cpu, bus); // execute HALT
        await Assert.That(cpu.Halted).IsTrue();
        await Assert.That(cpu.Halt).IsFalse().Because("/HALT is asserted (active low)");
        await Assert.That(cpu.PC.Value).IsEqualTo((ushort)0x0000);

        // A spin M1 is a plain 4-T opcode fetch with PC frozen and /HALT low.
        var spin = RunInstruction(cpu, bus);
        await Assert.That(spin).IsEqualTo(4);
        await Assert.That(cpu.Halt).IsFalse();
        await Assert.That(cpu.Halted).IsTrue();
        await Assert.That(cpu.PC.Value).IsEqualTo((ushort)0x0000);

        // /NMI wakes it: /HALT releases and the pushed return address is the
        // instruction after HALT (0x0001), not the HALT byte.
        cpu.Nmi = false;
        RunInstruction(cpu, bus);

        await Assert.That(cpu.Halted).IsFalse();
        await Assert.That(cpu.Halt).IsTrue();
        await Assert.That(cpu.PC.Value).IsEqualTo((ushort)0x0066);
        await Assert.That(bus.Ram[0xFFFD]).IsEqualTo((byte)0x01).Because("return = HALT + 1, low byte");
        await Assert.That(bus.Ram[0xFFFE]).IsEqualTo((byte)0x00).Because("return high byte");
    }

    // --- LD A,R -------------------------------------------------------

    [Test]
    public async Task LdARCopiesIff2IntoParityOverflow()
    {
        var (cpu, bus) = Boot(0xED, 0x5F); // LD A,R
        cpu.IFF2 = true;

        RunInstruction(cpu, bus);
        await Assert.That(cpu.Flags.ParityOverflow).IsTrue();

        var (cpu2, bus2) = Boot(0xED, 0x5F);
        cpu2.IFF2 = false;
        RunInstruction(cpu2, bus2);
        await Assert.That(cpu2.Flags.ParityOverflow).IsFalse();
    }

    // --- /BUSRQ -> /BUSAK -------------------------------------------

    [Test]
    public async Task BusRqReleasesTheBusAtTheNextMachineCycleBoundaryAndResumesOnRelease()
    {
        var (cpu, bus) = Boot(0x00, 0x00, 0x00, 0x00);

        RunInstruction(cpu, bus); // one NOP, so we are on a machine-cycle boundary
        var pcBeforeGrant = cpu.PC.Value;

        cpu.BusRq = false;
        bus.Tick(cpu);

        await Assert.That(cpu.BusReleased).IsTrue();
        await Assert.That(cpu.BusAk).IsFalse().Because("/BUSAK asserted");
        await Assert.That(cpu.MReq).IsTrue();
        await Assert.That(cpu.IoRq).IsTrue();
        await Assert.That(cpu.Rd).IsTrue();
        await Assert.That(cpu.Wr).IsTrue();

        // Held: the CPU makes no progress while the bus is granted.
        for (var i = 0; i < 12; i++)
        {
            bus.Tick(cpu);
        }

        await Assert.That(cpu.BusAk).IsFalse();
        await Assert.That(cpu.PC.Value).IsEqualTo(pcBeforeGrant);

        // Release: /BUSAK goes high and execution resumes.
        cpu.BusRq = true;
        bus.Tick(cpu);
        await Assert.That(cpu.BusAk).IsTrue();

        RunInstruction(cpu, bus);
        await Assert.That(cpu.PC.Value).IsEqualTo((ushort)(pcBeforeGrant + 1));
    }
}
