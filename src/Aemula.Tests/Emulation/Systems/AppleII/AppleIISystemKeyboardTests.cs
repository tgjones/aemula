using System.Threading.Tasks;
using Aemula.Emulation.Chips;
using Aemula.Emulation.Systems.AppleII;

namespace Aemula.Tests.Emulation.Systems.AppleII;

public class AppleIISystemKeyboardTests
{
    // Matches Ay53600Chip's DebounceTicks; +9 covers worst-case scan latency
    // for the X ring counter to reach the key's row.
    private const int TicksToLatch = 640 + 9;

    // Every character a PC typist can produce that the II+ keyboard can also
    // produce: pressing the key with that legend should type that same
    // character. This walks the host-character -> matrix map and the encoder's
    // ROM table together, so a mismatch in either shows up here.
    [Test]
    [Arguments('1')]
    [Arguments('2')]
    [Arguments('0')]
    [Arguments('!')]
    [Arguments('"')]
    [Arguments('#')]
    [Arguments('$')]
    [Arguments('%')]
    [Arguments('&')]
    [Arguments('\'')]
    [Arguments('(')]
    [Arguments(')')]
    [Arguments(':')]
    [Arguments('*')]
    [Arguments(';')]
    [Arguments('+')]
    [Arguments('-')]
    [Arguments('=')]
    [Arguments(',')]
    [Arguments('<')]
    [Arguments('.')]
    [Arguments('>')]
    [Arguments('/')]
    [Arguments('?')]
    [Arguments('@')]
    [Arguments('^')]
    [Arguments(']')]
    [Arguments('A')]
    [Arguments('z')]
    [Arguments('P')]
    [Arguments('M')]
    [Arguments('N')]
    public async Task LegendKeyTypesItsOwnCharacter(char expected)
    {
        var mapped = AppleIISystem.MapCharToMatrixPosition(expected);
        await Assert.That(mapped).IsNotNull();

        var (x, y, shift) = mapped!.Value;

        var chip = new Ay53600Chip { Shift = shift };
        chip.SetPressedKey(x, y);
        for (var i = 0; i < TicksToLatch; i++)
        {
            chip.Tick();
        }

        // Letters have no lowercase on the II+, so fold the expectation.
        var expectedCode = (byte)char.ToUpperInvariant(expected);
        await Assert.That(chip.Data).IsEqualTo(expectedCode);
    }

    [Test]
    [Arguments('[')]
    [Arguments('\\')]
    [Arguments('_')]
    [Arguments('{')]
    [Arguments('}')]
    [Arguments('|')]
    [Arguments('~')]
    [Arguments('`')]
    public async Task CharactersThePlusKeyboardCannotProduceMapToNothing(char unavailable)
    {
        await Assert.That(AppleIISystem.MapCharToMatrixPosition(unavailable)).IsNull();
    }

    [Test]
    public async Task PunctuationKeysReachBothLegendsOfASingleAppleKey()
    {
        // The Apple ":"/"*" key: unshifted from the PC ":" glyph, shifted from
        // the PC "*" glyph.
        await Assert.That(AppleIISystem.MapCharToMatrixPosition(':')).IsEqualTo((0, 8, false));
        await Assert.That(AppleIISystem.MapCharToMatrixPosition('*')).IsEqualTo((0, 8, true));

        // The Apple "-"/"=" key.
        await Assert.That(AppleIISystem.MapCharToMatrixPosition('-')).IsEqualTo((0, 9, false));
        await Assert.That(AppleIISystem.MapCharToMatrixPosition('=')).IsEqualTo((0, 9, true));
    }

    [Test]
    public async Task ControlAndArrowKeysMapToTheirFixedCrosspoints()
    {
        await Assert.That(AppleIISystem.MapCharToMatrixPosition(0x0D)).IsEqualTo((4, 9, false)); // Return.
        await Assert.That(AppleIISystem.MapCharToMatrixPosition(0x1B)).IsEqualTo((4, 3, false)); // Escape.
        await Assert.That(AppleIISystem.MapCharToMatrixPosition(0x08)).IsEqualTo((2, 8, false)); // Backspace.
        await Assert.That(AppleIISystem.MapCharToMatrixPosition(0x40000050)).IsEqualTo((2, 8, false)); // Left.
        await Assert.That(AppleIISystem.MapCharToMatrixPosition(0x4000004F)).IsEqualTo((2, 9, false)); // Right.
    }
}
