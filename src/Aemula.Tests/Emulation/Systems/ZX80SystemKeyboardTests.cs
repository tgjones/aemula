using System.Threading.Tasks;
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
    // pulls it low regardless of row/key state.

    [Test]
    public async Task ReadsBackPressedKeysOnlyWhenTheirRowIsSelected()
    {
        var system = new ZX80System();

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
    public async Task ShiftIsReadableFromEitherHostShiftKey()
    {
        var system = new ZX80System();

        system.OnKeyEvent(new KeyEvent { IsDown = true, Key = Key.LeftShift });
        await Assert.That(system.ReadKeyboardMatrixForTest(0xFEFF)).IsEqualTo((byte)0x3E);
        system.OnKeyEvent(new KeyEvent { IsDown = false, Key = Key.LeftShift });

        system.OnKeyEvent(new KeyEvent { IsDown = true, Key = Key.RightShift });
        await Assert.That(system.ReadKeyboardMatrixForTest(0xFEFF)).IsEqualTo((byte)0x3E);
    }

    [Test]
    public async Task MultipleSelectedRowsCombineIntoOneRead()
    {
        var system = new ZX80System();

        system.OnKeyEvent(new KeyEvent { IsDown = true, Key = Key.LeftShift }); // Row A8, column 0.
        system.OnKeyEvent(new KeyEvent { IsDown = true, Key = Key.A, Character = 'a' }); // Row A9, column 0.

        // A8 and A9 both selected (bits 8+9 low): both keys' column 0 clears.
        await Assert.That(system.ReadKeyboardMatrixForTest(0xFCFF)).IsEqualTo((byte)0x3E);
    }
}
