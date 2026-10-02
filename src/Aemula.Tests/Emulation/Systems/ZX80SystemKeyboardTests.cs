using System.Threading.Tasks;
using Aemula.Emulation.Output;
using Aemula.Emulation.Systems.ZX80;

namespace Aemula.Tests.Emulation.Systems;

public class ZX80SystemKeyboardTests
{
    // D7 in every expected byte below is EAR, live off the cassette buffer
    // (ZX80System.Cassette.cs) - these tests never call Tick(), so the
    // buffer sits at its default, unticked state (enabled, no signal),
    // which reads as 0 the same way a real idle/silent EAR line does (R1
    // biases it low, not high). D5 still reads 1 (no keyboard connection);
    // D6 reads 0 - the NTSC strap diode (D11) this build always has fitted
    // pulls it low regardless of row/key state. That is the US/NTSC board,
    // which is what these tests build; the UK board, which omits D11, is
    // checked separately below.

    [Test]
    public async Task OnlyTheNtscBoardPullsD6Low()
    {
        var ntsc = new ZX80System(TelevisionStandard.Ntsc);
        var pal = new ZX80System(TelevisionStandard.Pal);

        await Assert.That(ntsc.ReadKeyboardMatrixForTest(0xFEFE)).IsEqualTo((byte)0x3F);
        await Assert.That(pal.ReadKeyboardMatrixForTest(0xFEFE)).IsEqualTo((byte)0x7F);
    }

    [Test]
    public async Task TheDefaultBoardIsThePalOne()
    {
        var system = new ZX80System();

        await Assert.That(system.Television.Standard).IsEqualTo(TelevisionStandard.Pal);
        await Assert.That(system.ReadKeyboardMatrixForTest(0xFEFE)).IsEqualTo((byte)0x7F);
    }

    [Test]
    public async Task ReadsBackPressedKeysOnlyWhenTheirRowIsSelected()
    {
        var system = new ZX80System(TelevisionStandard.Ntsc);

        // Nothing pressed: every row reads all-1s in the low 5 bits.
        await Assert.That(system.ReadKeyboardMatrixForTest(0xFEFE)).IsEqualTo((byte)0x3F);

        system.OnKeyEvent(new KeyEvent { IsDown = true, Key = Key.A, Character = 'a' });

        // Row A9 (bit 9 low) selected: A is column 0, so bit 0 clears.
        await Assert.That(system.ReadKeyboardMatrixForTest(0xFDFF)).IsEqualTo((byte)0x3E);

        // A different row selected (A8, bit 8 low): A doesn't live there.
        await Assert.That(system.ReadKeyboardMatrixForTest(0xFEFF)).IsEqualTo((byte)0x3F);

        system.OnKeyEvent(new KeyEvent { IsDown = false, Key = Key.A, Character = 'a' });
        await Assert.That(system.ReadKeyboardMatrixForTest(0xFDFF)).IsEqualTo((byte)0x3F);
    }

    [Test]
    public async Task HostShiftAloneDoesNotPressZX80Shift()
    {
        var system = new ZX80System(TelevisionStandard.Ntsc);

        system.OnKeyEvent(new KeyEvent { IsDown = true, Key = Key.LeftShift });
        await Assert.That(system.ReadKeyboardMatrixForTest(0xFEFF)).IsEqualTo((byte)0x3F);
    }

    [Test]
    [Arguments(Key.LeftShift)]
    [Arguments(Key.RightShift)]
    public async Task HostShiftedLetterPressesZX80ShiftForItsGraphic(Key shiftKey)
    {
        var system = new ZX80System(TelevisionStandard.Ntsc);

        system.OnKeyEvent(new KeyEvent { IsDown = true, Key = shiftKey });
        system.OnKeyEvent(new KeyEvent { IsDown = true, Key = Key.A, Character = 'A' });

        await Assert.That(system.ReadKeyboardMatrixForTest(0xFEFF)).IsEqualTo((byte)0x3E); // SHIFT
        await Assert.That(system.ReadKeyboardMatrixForTest(0xFDFF)).IsEqualTo((byte)0x3E); // A

        // Letting go of Shift first leaves the key as it was pressed.
        system.OnKeyEvent(new KeyEvent { IsDown = false, Key = shiftKey });
        await Assert.That(system.ReadKeyboardMatrixForTest(0xFEFF)).IsEqualTo((byte)0x3E);

        system.OnKeyEvent(new KeyEvent { IsDown = false, Key = Key.A, Character = 'a' });
        await Assert.That(system.ReadKeyboardMatrixForTest(0xFEFF)).IsEqualTo((byte)0x3F);
        await Assert.That(system.ReadKeyboardMatrixForTest(0xFDFF)).IsEqualTo((byte)0x3F);
    }

    [Test]
    public async Task MultipleSelectedRowsCombineIntoOneRead()
    {
        var system = new ZX80System(TelevisionStandard.Ntsc);

        system.OnKeyEvent(new KeyEvent { IsDown = true, Key = Key.Z, Character = 'z' }); // Row A8, column 1.
        system.OnKeyEvent(new KeyEvent { IsDown = true, Key = Key.S, Character = 's' }); // Row A9, column 1.
        system.OnKeyEvent(new KeyEvent { IsDown = true, Key = Key.A, Character = 'a' }); // Row A9, column 0.

        // A8 and A9 both selected (bits 8+9 low): columns 0 and 1 clear.
        await Assert.That(system.ReadKeyboardMatrixForTest(0xFCFF)).IsEqualTo((byte)0x3C);
    }

    // Host characters the ZX80 has a key for type that key, pressing or
    // omitting ZX80 SHIFT to match regardless of whether host Shift was held.
    [Test]
    [Arguments('"', (Key)'\'', true, 5, 4, true)]
    [Arguments('=', (Key)'=', false, 6, 1, true)]
    [Arguments('+', (Key)'=', true, 6, 2, true)]
    [Arguments('*', Key.Digit8, true, 5, 0, true)]
    [Arguments(':', (Key)';', true, 0, 1, true)]
    [Arguments(',', (Key)',', false, 7, 1, true)]
    [Arguments('.', (Key)'.', false, 7, 1, false)]
    [Arguments('£', Key.Digit3, false, 7, 0, true)]
    [Arguments('1', (Key)'&', true, 3, 0, false)] // AZERTY: digits are shifted on the host.
    [Arguments('!', Key.Digit1, true, 3, 0, true)] // No ZX80 '!': SHIFT+1 (NOT) by position.
    [Arguments('B', Key.B, false, 7, 4, false)] // --input sends letters with no Shift event.
    public async Task HostCharacterSelectsZX80Key(char character, Key key, bool hostShift, int row, int column, bool zx80Shift)
    {
        var system = new ZX80System(TelevisionStandard.Ntsc);

        if (hostShift)
        {
            system.OnKeyEvent(new KeyEvent { IsDown = true, Key = Key.LeftShift });
        }
        system.OnKeyEvent(new KeyEvent { IsDown = true, Key = key, Character = character });

        await AssertOnlyPressed(system, row, column, zx80Shift);

        system.OnKeyEvent(new KeyEvent { IsDown = false, Key = key });
        await AssertOnlyPressed(system, -1, -1, false);
    }

    [Test]
    [Arguments(Key.Left, 3, 4)]
    [Arguments(Key.Down, 4, 4)]
    [Arguments(Key.Up, 4, 3)]
    [Arguments(Key.Right, 4, 2)]
    [Arguments(Key.Home, 4, 1)]
    [Arguments(Key.Backspace, 4, 0)]
    [Arguments(Key.Delete, 4, 0)]
    public async Task EditingKeysPressTheirShiftedDigit(Key key, int row, int column)
    {
        var system = new ZX80System(TelevisionStandard.Ntsc);

        system.OnKeyEvent(new KeyEvent { IsDown = true, Key = key });

        await AssertOnlyPressed(system, row, column, zx80Shift: true);
    }

    [Test]
    public async Task ShiftStaysDownWhileAnyHeldKeyNeedsIt()
    {
        var system = new ZX80System(TelevisionStandard.Ntsc);

        system.OnKeyEvent(new KeyEvent { IsDown = true, Key = (Key)'=', Character = '=' });
        system.OnKeyEvent(new KeyEvent { IsDown = true, Key = (Key)'/', Character = '/' });
        system.OnKeyEvent(new KeyEvent { IsDown = false, Key = (Key)'=' });

        await AssertOnlyPressed(system, 0, 4, zx80Shift: true);
    }

    private static async Task AssertOnlyPressed(ZX80System system, int row, int column, bool zx80Shift)
    {
        for (var r = 0; r < 8; r++)
        {
            var expected = 0x1F;
            if (r == row)
            {
                expected &= ~(1 << column);
            }
            if (r == 0 && zx80Shift)
            {
                expected &= ~1;
            }

            var address = (ushort)(0xFFFF & ~(0x100 << r));
            await Assert.That(system.ReadKeyboardMatrixForTest(address) & 0x1F).IsEqualTo(expected);
        }
    }
}
