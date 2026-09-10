using System.Threading.Tasks;
using Aemula.Emulation.Systems.AppleI;

namespace Aemula.Tests.Emulation.Systems.AppleI;

public class AppleISystemTests
{
    // WozMon's reset path (CLD/CLI/init DSP+KBDCR+DSPCR, then falling
    // through NOTCR/ESCAPE/GETLINE with no line typed yet) lands in
    // NEXTCHAR - "LDA KBDCR ($D011); BPL NEXTCHAR" - and spins there until a
    // key arrives. Reaching and staying at that exact fetch address is the
    // clearest sign the CPU, address decode, DRAM, ROM and PIA are all
    // wired correctly end to end, without needing video or a keyboard yet.
    private const ushort NextCharLoop = 0xFF29;

    // WozMon's reset path now echoes "\" and CR through a display handshake
    // that genuinely blocks until the character rings accept each byte, and
    // the video counters only start turning at reset (not at power-on), so
    // the first echo can stall for up to a full frame-long ring rotation.
    private const int MasterTicksPerFrame = 262 * 65 * 14;

    [Test]
    public async Task RunsResetVectorIntoWozMonNextCharLoop()
    {
        var system = new AppleISystem();
        system.LoadProgram("");

        var maxCycles = MasterTicksPerFrame * 3;
        var cycles = 0;
        var reachedNextCharLoop = false;

        while (cycles < maxCycles)
        {
            system.Tick();

            if (system.Cpu.Sync && system.Cpu.Address == NextCharLoop)
            {
                reachedNextCharLoop = true;
                break;
            }

            cycles++;
        }

        await Assert.That(reachedNextCharLoop).IsTrue();
    }

    [Test]
    public async Task StaysInNextCharLoopWithNoKeyPressed()
    {
        // Once idle, WozMon should keep re-fetching NEXTCHAR every loop
        // iteration rather than wandering off - there's no key event fed
        // in here, so KBDCR's ready bit never sets.
        var system = new AppleISystem();
        system.LoadProgram("");

        for (var i = 0; i < MasterTicksPerFrame * 3; i++)
        {
            system.Tick();
        }

        var sawNextCharFetch = false;

        for (var i = 0; i < 1_000; i++)
        {
            system.Tick();

            if (system.Cpu.Sync && system.Cpu.Address == NextCharLoop)
            {
                sawNextCharFetch = true;
                break;
            }
        }

        await Assert.That(sawNextCharFetch).IsTrue();

        // KBDCR read back through the same PIA/address-decode path the CPU
        // itself just used - control-register writes only keep bits 0-5
        // (Mos6820Chip.WriteRegister), so the $A7 the reset path wrote
        // reads back as $27 with the ready flag (bit 7) clear.
        await Assert.That(system.ReadByteDebug(0xD011)).IsEqualTo((byte)0x27);
    }

    [Test]
    public async Task BothBanksAreReadWriteWithBankBAt1000()
    {
        // The fully-populated bare board: bank A at $0000-$0FFF, bank B
        // jumpered right above it at $1000-$1FFF for a contiguous 8K.
        var system = new AppleISystem();
        system.LoadProgram("");

        system.WriteByteDebug(0x0042, 0x11); // Bank A.
        system.WriteByteDebug(0x1042, 0x22); // Bank B at $1000.

        await Assert.That(system.ReadByteDebug(0x0042)).IsEqualTo((byte)0x11);
        await Assert.That(system.ReadByteDebug(0x1042)).IsEqualTo((byte)0x22);

        // Bank B is not also at $E000 - that range is unpopulated.
        system.WriteByteDebug(0xE000, 0x4C);
        await Assert.That(system.ReadByteDebug(0xE000)).IsEqualTo((byte)0xFF);
    }

    [Test]
    public async Task BankBAtE000MakesE000ReadWriteAndLeaves1000OpenBus()
    {
        // The split configuration Integer BASIC needs: bank B jumpered up to
        // $E000-$EFFF, which leaves $1000-$1FFF unpopulated.
        var system = new AppleISystem(AppleISystemOptions.EquippedForBasic);
        system.LoadProgram("");

        system.WriteByteDebug(0xE000, 0x4C); // BASIC's cold-start JMP would land here.
        system.WriteByteDebug(0xEFFF, 0xA5);

        await Assert.That(system.ReadByteDebug(0xE000)).IsEqualTo((byte)0x4C);
        await Assert.That(system.ReadByteDebug(0xEFFF)).IsEqualTo((byte)0xA5);

        // $1000-$1FFF has nothing behind it now - reads float high, writes go
        // nowhere.
        system.WriteByteDebug(0x1042, 0x22);
        await Assert.That(system.ReadByteDebug(0x1042)).IsEqualTo((byte)0xFF);

        // Bank B at $E000 is its own 4K block - writing it must not leak into
        // the PIA block below it, and the Monitor ROM above it still reads as
        // ROM.
        system.WriteByteDebug(0xE042, 0x99);
        await Assert.That(system.ReadByteDebug(0xF000)).IsEqualTo(system.ReadByteDebug(0xFF00));
    }

    [Test]
    public async Task RomMirrorsAcrossWholeChipSelectBlock()
    {
        // ICA1/ICA2 only decode A0-A7 - CSF (the 74154's Y15, $F000-$FFFF)
        // is the only chip-select input they get, so the 256-byte image
        // repeats at every page of that 4K block, not just at $FF00-$FFFF.
        var system = new AppleISystem();
        system.LoadProgram("");

        await Assert.That(system.ReadByteDebug(0xF000)).IsEqualTo(system.ReadByteDebug(0xFF00));
        await Assert.That(system.ReadByteDebug(0xF0FC)).IsEqualTo(system.ReadByteDebug(0xFFFC));
    }
}
