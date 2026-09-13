using System.Threading.Tasks;
using Aemula.Emulation.Systems.ZX80;

namespace Aemula.Tests.Emulation.Systems;

// The cassette EAR/MIC wiring (ZX80System.Cassette.cs): IC10's sixth buffer
// carrying EAR onto D7 during a real keyboard-style I/O read, and MIC taking
// the raw SYNC node the way R35/C14 tap it on the real board. The rig-level
// wiring (PeripheralRequests -> a CassetteDeck with a "cassette" media bay)
// is covered by MediaBayTests instead, alongside the Apple I's own.
public class ZX80SystemCassetteTests
{
    // Ticks until the CPU is mid an actual keyboard/cassette I/O read (/RD
    // and /IORQ both asserted, A0 low) - exactly the condition GetKbdSignal
    // represents and DoCpuMemoryAccess already gates ReadKeyboardMatrix on,
    // so it's also exactly when IC10 (and so the EAR buffer) is enabled. The
    // ROM's idle loop polls the keyboard continuously, so this window shows
    // up well within a boot.
    private static bool TickUntilKeyboardRead(ZX80System system, int tickBudget)
    {
        for (var i = 0; i < tickBudget; i++)
        {
            system.Tick();

            if (!system.Cpu.Rd && !system.Cpu.IoRq && (system.Cpu.Address & 1) == 0)
            {
                return true;
            }
        }

        return false;
    }

    [Test]
    public async Task EarHighReadsAsD7SetDuringAKeyboardRead()
    {
        var system = new ZX80System();
        system.CassetteInput = () => 1f;

        await Assert.That(TickUntilKeyboardRead(system, 40_000_000)).IsTrue();
        await Assert.That(system.ReadKeyboardMatrixForTest(system.Cpu.Address) & 0x80).IsEqualTo(0x80);
    }

    [Test]
    public async Task EarSilentReadsAsD7ClearDuringAKeyboardRead()
    {
        var system = new ZX80System();
        system.CassetteInput = () => 0f;

        await Assert.That(TickUntilKeyboardRead(system, 40_000_000)).IsTrue();
        await Assert.That(system.ReadKeyboardMatrixForTest(system.Cpu.Address) & 0x80).IsEqualTo(0x00);
    }

    [Test]
    public async Task MicOutputTracksTheRawSyncNode()
    {
        var system = new ZX80System();

        var sawHigh = false;
        var sawLow = false;
        system.CassetteOutput = level =>
        {
            if (level > 0f)
            {
                sawHigh = true;
            }
            else
            {
                sawLow = true;
            }
        };

        // A real HSYNC pulse train is both polarities, repeating - long
        // enough to cover several scanlines' worth of SYNC toggling
        // (ZX80SystemVideoTests sees transitions well within this budget).
        for (var i = 0; i < 5_000_000; i++)
        {
            system.Tick();
        }

        await Assert.That(sawHigh).IsTrue();
        await Assert.That(sawLow).IsTrue();
    }
}
