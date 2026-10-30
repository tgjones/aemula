using System.Threading.Tasks;
using Aemula.Emulation.Systems.BbcMicro;

namespace Aemula.Tests.Emulation.Systems.BbcMicro;

// The printer, user port and analogue port have nothing attached; the OS
// probes them while booting and must see idle, pulled-up lines.
public class BbcMicroPortsTests
{
    private static BbcMicroSystem Boot()
    {
        var system = new BbcMicroSystem(null, 100);

        for (var tick = 0; tick < 6_000_000; tick++)
        {
            system.Tick();
        }

        return system;
    }

    [Test]
    public async Task IdlePortsReadAsPulledUpAfterBoot()
    {
        var system = Boot();

        // PB4/PB5 fire buttons released, PB6/PB7 speech lines high.
        await Assert.That(system.SystemVia.PortB & 0xF0).IsEqualTo(0xF0);
        await Assert.That(system.UserVia.PortB).IsEqualTo((byte)0xFF);
    }

    [Test]
    public async Task FireButtonsPullTheirSystemViaPinsLow()
    {
        var system = Boot();

        system.SetJoystickFire(0, true);
        await Assert.That(system.SystemVia.PortB & 0x30).IsEqualTo(0x20);

        system.SetJoystickFire(1, true);
        await Assert.That(system.SystemVia.PortB & 0x30).IsEqualTo(0x00);

        system.SetJoystickFire(0, false);
        system.SetJoystickFire(1, false);
        await Assert.That(system.SystemVia.PortB & 0x30).IsEqualTo(0x30);
    }
}
