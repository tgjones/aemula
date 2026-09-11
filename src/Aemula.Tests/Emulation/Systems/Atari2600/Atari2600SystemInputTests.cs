using System.Threading.Tasks;
using Aemula.Emulation.Systems.Atari2600;

namespace Aemula.Tests.Emulation.Systems.Atari2600;

// Host keyboard -> left-joystick input (Atari2600System.Input.cs): arrow keys
// drive the four SWCHA direction bits (RIOT port A, top nibble) and space
// drives INPT4 (TIA I-pin 4). Every line is active-low, idle high.
public class Atari2600SystemInputTests
{
    private static void KeyDown(Atari2600System system, Key key) =>
        system.OnKeyEvent(new KeyEvent { IsDown = true, Key = key });

    private static void KeyUp(Atari2600System system, Key key) =>
        system.OnKeyEvent(new KeyEvent { IsDown = false, Key = key });

    [Test]
    public async Task IdleInputSitsAtAllOnes()
    {
        var system = new Atari2600System();

        // SWCHA top nibble (player 0 stick) and INPT4 bit released.
        await Assert.That(system.Riot.PA & 0xF0).IsEqualTo(0xF0);
        await Assert.That(system.Tia.I & 0b0001_0000).IsEqualTo(0b0001_0000);
    }

    [Test]
    [Arguments(Key.Up, 0b0001_0000)]
    [Arguments(Key.Down, 0b0010_0000)]
    [Arguments(Key.Left, 0b0100_0000)]
    [Arguments(Key.Right, 0b1000_0000)]
    public async Task ArrowKeyPullsItsSwchaBitLowThenReleasesIt(Key key, int bit)
    {
        var system = new Atari2600System();

        KeyDown(system, key);
        await Assert.That(system.Riot.PA & bit).IsEqualTo(0);
        // The other three direction bits are untouched.
        await Assert.That(system.Riot.PA & (0xF0 & ~bit)).IsEqualTo(0xF0 & ~bit);

        KeyUp(system, key);
        await Assert.That(system.Riot.PA & bit).IsEqualTo(bit);
    }

    [Test]
    public async Task DiagonalHoldsTwoDirectionBitsLowAtOnce()
    {
        var system = new Atari2600System();

        KeyDown(system, Key.Up);
        KeyDown(system, Key.Left);

        // Up (bit 4) + Left (bit 6) low, Down + Right still high.
        await Assert.That(system.Riot.PA & 0xF0).IsEqualTo(0b1010_0000);

        KeyUp(system, Key.Up);
        await Assert.That(system.Riot.PA & 0xF0).IsEqualTo(0b1011_0000);
    }

    [Test]
    public async Task SpacePullsInpt4LowThenReleasesIt()
    {
        var system = new Atari2600System();

        KeyDown(system, Key.Space);
        await Assert.That(system.Tia.I & 0b0001_0000).IsEqualTo(0);

        KeyUp(system, Key.Space);
        await Assert.That(system.Tia.I & 0b0001_0000).IsEqualTo(0b0001_0000);
    }

    [Test]
    public async Task UnmappedKeysLeaveInputUntouched()
    {
        var system = new Atari2600System();

        KeyDown(system, Key.A);
        KeyDown(system, Key.Return);

        await Assert.That(system.Riot.PA & 0xF0).IsEqualTo(0xF0);
        await Assert.That(system.Tia.I & 0b0001_0000).IsEqualTo(0b0001_0000);
    }
}
