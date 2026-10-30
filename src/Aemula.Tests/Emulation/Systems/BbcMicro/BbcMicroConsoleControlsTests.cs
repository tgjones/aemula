using System.Linq;
using System.Threading.Tasks;
using Aemula.Emulation.Systems;
using Aemula.Emulation.Systems.BbcMicro;

namespace Aemula.Tests.Emulation.Systems.BbcMicro;

public class BbcMicroConsoleControlsTests
{
    [Test]
    public async Task SystemIsRegisteredAndCreatesADebugger()
    {
        var rig = EmulatedSystems.FindById("bbcmicro")!.Build(ExpansionSlotConfiguration.Empty);

        await Assert.That(rig.System.CreateDebugger()).IsNotNull();
    }

    [Test]
    public async Task BreakRestartsTheMachine()
    {
        var system = new BbcMicroSystem(null, 100);

        for (var tick = 0; tick < 6_000_000; tick++)
        {
            system.Tick();
        }

        // A marker on the Mode 7 screen, which the OS clears on restarting.
        const ushort marker = 0x7C00 + 40 * 10;
        system.WriteByteDebug(marker, (byte)'Z');

        var breakKey = system.ConsoleControls.Single(c => c.Mnemonic == "break");
        breakKey.Value = true;
        breakKey.Value = false;

        for (var tick = 0; tick < 6_000_000; tick++)
        {
            system.Tick();
        }

        await Assert.That(system.ReadByteDebug(marker)).IsNotEqualTo((byte)'Z');
    }

    [Test]
    public async Task LedsAreLitWhileTheirLatchBitIsLow()
    {
        var system = new BbcMicroSystem(null, 100);

        var latch = system._ic32;
        latch.G = true;
        latch.A0 = false;
        latch.A1 = true;
        latch.A2 = true;
        latch.D = false;
        latch.G = false;
        latch.G = true;

        latch.A0 = true;
        latch.D = true;
        latch.G = false;
        latch.G = true;

        await Assert.That(system.CapsLockLed).IsTrue();
        await Assert.That(system.ShiftLockLed).IsFalse();
    }
}
