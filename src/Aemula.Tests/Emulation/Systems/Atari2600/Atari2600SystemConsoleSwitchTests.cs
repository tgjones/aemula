using System.Linq;
using System.Threading.Tasks;
using Aemula.Emulation.Systems;
using Aemula.Emulation.Systems.Atari2600;

namespace Aemula.Tests.Emulation.Systems.Atari2600;

// The console panel exposed through EmulatedSystem.ConsoleControls, read by
// the program as SWCHB (RIOT port B). RESET/SELECT are active-low momentary
// buttons; TV type and the two difficulty switches latch.
public class Atari2600SystemConsoleSwitchTests
{
    private const int Reset = 0b0000_0001;
    private const int Select = 0b0000_0010;
    private const int Color = 0b0000_1000;
    private const int LeftDifficulty = 0b0100_0000;
    private const int RightDifficulty = 0b1000_0000;

    private static ConsoleControl Control(Atari2600System system, string label) =>
        system.ConsoleControls.Single(c => c.Label == label);

    [Test]
    public async Task PowersOnWithNothingPressedColourTvAndBothDifficultiesAtB()
    {
        var system = new Atari2600System();

        // RESET + SELECT released (active-low, so 1), TV type = colour, both
        // difficulty switches at B.
        await Assert.That(system.Riot.PB).IsEqualTo((byte)0b0000_1011);
    }

    [Test]
    public async Task ExposesTheFivePanelControls()
    {
        var system = new Atari2600System();

        var labels = system.ConsoleControls.Select(c => c.Label).ToArray();

        // Left-to-right in real-console panel order.
        await Assert.That(labels).IsEquivalentTo(
            ["TV Type", "Left Diff.", "Right Diff.", "Select", "Reset"]);
    }

    [Test]
    [Arguments("Reset", Reset)]
    [Arguments("Select", Select)]
    public async Task MomentaryButtonPullsItsBitLowWhileHeldOnly(string label, int bit)
    {
        var system = new Atari2600System();
        var control = Control(system, label);

        await Assert.That(control.Kind).IsEqualTo(ConsoleControl.ControlKind.Momentary);
        await Assert.That(control.Value).IsFalse();
        await Assert.That(system.Riot.PB & bit).IsEqualTo(bit);

        control.Value = true;
        await Assert.That(system.Riot.PB & bit).IsEqualTo(0);
        // Nothing else on the port moved.
        await Assert.That(system.Riot.PB & ~bit).IsEqualTo(0b0000_1011 & ~bit);

        control.Value = false;
        await Assert.That(control.Value).IsFalse();
        await Assert.That(system.Riot.PB & bit).IsEqualTo(bit);
    }

    [Test]
    [Arguments("TV Type", Color)]
    [Arguments("Left Diff.", LeftDifficulty)]
    [Arguments("Right Diff.", RightDifficulty)]
    public async Task ToggleTracksItsSwchbBit(string label, int bit)
    {
        var system = new Atari2600System();
        var control = Control(system, label);

        await Assert.That(control.Kind).IsEqualTo(ConsoleControl.ControlKind.Toggle);

        // Whatever the power-on position is, the control reports it faithfully.
        await Assert.That(control.Value).IsEqualTo((system.Riot.PB & bit) != 0);

        control.Value = true;
        await Assert.That(system.Riot.PB & bit).IsEqualTo(bit);

        control.Value = false;
        await Assert.That(system.Riot.PB & bit).IsEqualTo(0);
    }

    [Test]
    public async Task ToggleAndMomentaryControlsDoNotDisturbEachOther()
    {
        var system = new Atari2600System();

        Control(system, "Left Diff.").Value = true;
        Control(system, "Right Diff.").Value = true;
        Control(system, "Reset").Value = true;

        // Reset low, Select still released, both difficulties high, colour set.
        await Assert.That(system.Riot.PB).IsEqualTo((byte)0b1100_1010);

        Control(system, "Reset").Value = false;
        await Assert.That(system.Riot.PB).IsEqualTo((byte)0b1100_1011);
    }
}
