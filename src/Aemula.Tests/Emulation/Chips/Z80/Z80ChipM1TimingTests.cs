using System.Threading.Tasks;
using Aemula.Emulation.Chips.Z80;

namespace Aemula.Tests.Emulation.Chips.Z80;

// Hand-written checks of the M1 opcode-fetch machine cycle and its DRAM-refresh
// second half, driving raw CLK edges. The half-T-state edges follow the Zilog
// Z80 CPU User Manual (UM0080) M1 timing diagram:
//
//   T1 up    /M1 low, address bus = PC, PC incremented
//   T1 down  /MREQ low, /RD low
//   T3 up    opcode latched off the data bus; /MREQ //RD //M1 released;
//            /RFSH low, address bus = I:R, R does its 7-bit increment
//   T3 down  /MREQ pulses low again for the refresh
//   T4 up    refresh /MREQ released
//
// One T-state is one full CLK period: chip.Clk = true; chip.Clk = false;
public class Z80ChipM1TimingTests
{
    [Test]
    public async Task M1OpcodeFetchDrivesTheFullEdgeSequence()
    {
        var cpu = new Z80Chip();
        cpu.PC.Value = 0x1234;

        // I:R is the refresh address latched in M1's second half. R = 0xFF checks
        // that its auto-increment wraps within bits 0-6 and leaves bit 7 alone.
        cpu.I = 0x7A;
        cpu.R = 0xFF;

        // --- T1 rising: /M1 low, address = PC, PC incremented -------------
        cpu.Clk = true;
        await Assert.That(cpu.CurrentState).IsEqualTo(Z80Chip.TState.T1);
        await Assert.That(cpu.M1).IsFalse();
        await Assert.That(cpu.Address).IsEqualTo((ushort)0x1234);
        await Assert.That(cpu.PC.Value).IsEqualTo((ushort)0x1235);
        await Assert.That(cpu.Rfsh).IsTrue();
        await Assert.That(cpu.MReq).IsTrue();
        await Assert.That(cpu.Rd).IsTrue();

        // --- T1 falling: /MREQ and /RD low ------------------------------
        cpu.Clk = false;
        await Assert.That(cpu.MReq).IsFalse();
        await Assert.That(cpu.Rd).IsFalse();

        // --- T2 ---------------------------------------------------------
        cpu.Clk = true;
        await Assert.That(cpu.CurrentState).IsEqualTo(Z80Chip.TState.T2);
        cpu.Clk = false;

        // Memory places the opcode on the bus before T3's rising edge.
        cpu.Data = 0x40; // LD B,B - a real, in-scope opcode that only acts at T4.

        // --- T3 rising: opcode latched; M1 control lines released; -------
        //     /RFSH low with I:R on the bus; R 7-bit auto-increment.
        cpu.Clk = true;
        await Assert.That(cpu.CurrentState).IsEqualTo(Z80Chip.TState.T3);
        await Assert.That(cpu.InstructionRegister).IsEqualTo((byte)0x40);
        await Assert.That(cpu.M1).IsTrue();
        await Assert.That(cpu.Rd).IsTrue();
        await Assert.That(cpu.MReq).IsTrue();
        await Assert.That(cpu.Rfsh).IsFalse();
        await Assert.That(cpu.Address).IsEqualTo((ushort)0x7AFF);
        await Assert.That(cpu.R).IsEqualTo((byte)0x80);

        // --- T3 falling: refresh /MREQ pulse low --------------------------
        cpu.Clk = false;
        await Assert.That(cpu.MReq).IsFalse();
        await Assert.That(cpu.Rfsh).IsFalse();
        await Assert.That(cpu.Address).IsEqualTo((ushort)0x7AFF);

        // --- T4 rising: refresh /MREQ released --------------------------
        cpu.Clk = true;
        await Assert.That(cpu.CurrentState).IsEqualTo(Z80Chip.TState.T4);
        await Assert.That(cpu.MReq).IsTrue();
        cpu.Clk = false;
    }

    [Test]
    public async Task RefreshRegisterIncrementKeepsBit7()
    {
        var cpu = new Z80Chip();
        cpu.I = 0x12;
        cpu.R = 0xCE; // bit 7 set, low 7 bits = 0x4E

        // Clock to T3 rising, where the refresh address is driven and R steps.
        cpu.Clk = true;  // T1
        cpu.Clk = false;
        cpu.Clk = true;  // T2
        cpu.Clk = false;
        cpu.Data = 0x00; // NOP
        cpu.Clk = true;  // T3 rising

        // The refresh address is I in the high byte and the whole R register -
        // bit 7 included - in the low byte, sampled before the increment. R then
        // steps to 0xCF: low 7 bits 0x4E -> 0x4F, bit 7 untouched.
        await Assert.That(cpu.Address).IsEqualTo((ushort)0x12CE);
        await Assert.That(cpu.R).IsEqualTo((byte)0xCF);
        cpu.Clk = false;
    }
}
