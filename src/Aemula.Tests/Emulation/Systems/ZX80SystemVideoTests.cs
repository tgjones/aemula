using System.Threading.Tasks;
using Aemula.Emulation.Systems.ZX80;

namespace Aemula.Tests.Emulation.Systems;

public class ZX80SystemVideoTests
{
    [Test]
    public async Task GeneratesRepeatingHSyncPulsesAndCyclesTheScanlineCounter()
    {
        var system = new ZX80System();

        var sawSyncHigh = false;
        var sawSyncLow = false;
        var syncTransitions = 0;
        var previousSync = false;
        var maxScanline = 0;
        var sawScanlineWrap = false;
        var previousScanline = 0;

        // Long enough to run past the ROM's init code and well into video
        // generation (the smoke test needs ~1.6M ticks just to reach the
        // display-file execution trick).
        for (var i = 0; i < 20_000_000; i++)
        {
            system.Tick();

            var sync = system.SyncSignalForTest;
            if (sync)
            {
                sawSyncHigh = true;
            }
            else
            {
                sawSyncLow = true;
            }

            if (sync != previousSync)
            {
                syncTransitions++;
            }

            previousSync = sync;

            var scanline = system.ScanlineForTest;
            if (scanline > maxScanline)
            {
                maxScanline = scanline;
            }

            if (previousScanline == 7 && scanline == 0)
            {
                sawScanlineWrap = true;
            }

            previousScanline = scanline;
        }

        // A real HSYNC pulse train is both polarities, repeating - not a
        // signal stuck high or low, and not a single one-off pulse.
        await Assert.That(sawSyncHigh).IsTrue();
        await Assert.That(sawSyncLow).IsTrue();
        await Assert.That(syncTransitions).IsGreaterThan(10);

        // The 3-bit scanline counter should reach its full 0-7 range and
        // wrap back to 0 - one pass per character row.
        await Assert.That(maxScanline).IsEqualTo(7);
        await Assert.That(sawScanlineWrap).IsTrue();
    }
}
