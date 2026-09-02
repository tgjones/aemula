using System.Threading.Tasks;
using Aemula.Emulation.Systems.Nes;

namespace Aemula.Tests.Emulation.Systems.Nes;

public class NesControllerTests
{
    // One $4016/$4017 read: the console samples the serial line, then the
    // mainboard pulses the shift clock.
    private static bool Read(NesController controller)
    {
        var bit = controller.SerialData;
        controller.Clock = true;
        controller.Clock = false;
        return bit;
    }

    // The standard "write 1 then 0 to $4016" strobe that latches a snapshot.
    private static void Strobe(NesController controller)
    {
        controller.Latch = true;
        controller.Latch = false;
    }

    [Test]
    public async Task ReadsButtonsInShiftOutOrderAfterStrobe()
    {
        var controller = new NesController
        {
            Buttons = NesButton.A | NesButton.Start | NesButton.Right,
        };

        Strobe(controller);

        // A, B, Select, Start, Up, Down, Left, Right.
        await Assert.That(Read(controller)).IsEqualTo(true);  // A
        await Assert.That(Read(controller)).IsEqualTo(false); // B
        await Assert.That(Read(controller)).IsEqualTo(false); // Select
        await Assert.That(Read(controller)).IsEqualTo(true);  // Start
        await Assert.That(Read(controller)).IsEqualTo(false); // Up
        await Assert.That(Read(controller)).IsEqualTo(false); // Down
        await Assert.That(Read(controller)).IsEqualTo(false); // Left
        await Assert.That(Read(controller)).IsEqualTo(true);  // Right
    }

    [Test]
    public async Task ReturnsHighFromTheNinthReadOnward()
    {
        var controller = new NesController { Buttons = NesButton.None };

        Strobe(controller);

        for (var i = 0; i < 8; i++)
        {
            await Assert.That(Read(controller)).IsEqualTo(false);
        }

        for (var i = 0; i < 4; i++)
        {
            await Assert.That(Read(controller)).IsEqualTo(true);
        }
    }

    [Test]
    public async Task WhileLatchHeldSerialLineTracksLiveAButton()
    {
        var controller = new NesController();

        controller.Latch = true;

        await Assert.That(controller.SerialData).IsEqualTo(false);

        controller.Buttons = NesButton.A;
        await Assert.That(controller.SerialData).IsEqualTo(true);

        controller.Buttons = NesButton.B;
        await Assert.That(controller.SerialData).IsEqualTo(false);
    }

    [Test]
    public async Task ReStrobingReloadsFromCurrentButtons()
    {
        var controller = new NesController { Buttons = NesButton.A };

        Strobe(controller);
        await Assert.That(Read(controller)).IsEqualTo(true); // A
        await Assert.That(Read(controller)).IsEqualTo(false); // B, register has moved on

        controller.Buttons = NesButton.B;
        Strobe(controller);

        await Assert.That(Read(controller)).IsEqualTo(false); // A no longer held
        await Assert.That(Read(controller)).IsEqualTo(true);  // B
    }

    [Test]
    public async Task ClockEdgesWhileLatchedDoNotAdvanceTheRegister()
    {
        var controller = new NesController { Buttons = NesButton.Start };

        controller.Latch = true;

        // Pulsing the clock during the parallel load must not shift anything.
        controller.Clock = true;
        controller.Clock = false;

        controller.Latch = false;

        await Assert.That(Read(controller)).IsEqualTo(false); // A
        await Assert.That(Read(controller)).IsEqualTo(false); // B
        await Assert.That(Read(controller)).IsEqualTo(false); // Select
        await Assert.That(Read(controller)).IsEqualTo(true);  // Start
    }
}
