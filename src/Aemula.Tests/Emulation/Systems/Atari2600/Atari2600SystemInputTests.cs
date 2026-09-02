using System.Threading.Tasks;
using Aemula.Emulation.Systems.Atari2600;
using Hexa.NET.SDL3;

namespace Aemula.Tests.Emulation.Systems.Atari2600;

// Host keyboard -> left-joystick input (Atari2600System.Input.cs): arrow keys
// drive the four SWCHA direction bits (RIOT port A, top nibble) and space
// drives INPT4 (TIA I-pin 4). Every line is active-low, idle high.
public class Atari2600SystemInputTests
{
    private const int SdlkRight = 0x4000004F;
    private const int SdlkLeft = 0x40000050;
    private const int SdlkDown = 0x40000051;
    private const int SdlkUp = 0x40000052;
    private const int SdlkSpace = 0x20;

    private static void KeyDown(Atari2600System system, int key) =>
        system.OnKeyEvent(new SDLKeyboardEvent { Type = SDLEventType.KeyDown, Key = key });

    private static void KeyUp(Atari2600System system, int key) =>
        system.OnKeyEvent(new SDLKeyboardEvent { Type = SDLEventType.KeyUp, Key = key });

    [Test]
    public async Task IdleInputSitsAtAllOnes()
    {
        var system = new Atari2600System();

        // SWCHA top nibble (player 0 stick) and INPT4 bit released.
        await Assert.That(system.Riot.PA & 0xF0).IsEqualTo(0xF0);
        await Assert.That(system.Tia.I & 0b0001_0000).IsEqualTo(0b0001_0000);
    }

    [Test]
    [Arguments(SdlkUp, 0b1000_0000)]
    [Arguments(SdlkDown, 0b0100_0000)]
    [Arguments(SdlkLeft, 0b0010_0000)]
    [Arguments(SdlkRight, 0b0001_0000)]
    public async Task ArrowKeyPullsItsSwchaBitLowThenReleasesIt(int key, int bit)
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

        KeyDown(system, SdlkUp);
        KeyDown(system, SdlkLeft);

        // Up + Left low, Down + Right still high.
        await Assert.That(system.Riot.PA & 0xF0).IsEqualTo(0b0101_0000);

        KeyUp(system, SdlkUp);
        await Assert.That(system.Riot.PA & 0xF0).IsEqualTo(0b1101_0000);
    }

    [Test]
    public async Task SpacePullsInpt4LowThenReleasesIt()
    {
        var system = new Atari2600System();

        KeyDown(system, SdlkSpace);
        await Assert.That(system.Tia.I & 0b0001_0000).IsEqualTo(0);

        KeyUp(system, SdlkSpace);
        await Assert.That(system.Tia.I & 0b0001_0000).IsEqualTo(0b0001_0000);
    }

    [Test]
    public async Task UnmappedKeysLeaveInputUntouched()
    {
        var system = new Atari2600System();

        KeyDown(system, 'a');
        KeyDown(system, 0x0D); // Return.

        await Assert.That(system.Riot.PA & 0xF0).IsEqualTo(0xF0);
        await Assert.That(system.Tia.I & 0b0001_0000).IsEqualTo(0b0001_0000);
    }
}
