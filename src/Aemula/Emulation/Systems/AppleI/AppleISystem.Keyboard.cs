namespace Aemula.Emulation.Systems.AppleI;

// The keyboard: the B4 connector wires straight into the PIA (PA0-PA7 for
// data, CA1 for the strobe) with no encoder chip in between at all -
// confirmed on the schematic. So, unlike AppleII/AppleIISystem.Keyboard.cs's
// AY-5-3600 matrix simulation, this is just a host-key-to-ASCII map and a
// strobe pulse.
public sealed partial class AppleISystem
{
    public override void OnKeyEvent(KeyEvent keyEvent)
    {
        if (!keyEvent.IsDown || keyEvent.Character is not { } character)
        {
            return;
        }

        var ascii = MapCharToAscii(character);
        if (ascii is null)
        {
            return;
        }

        // The real keyboard encoder outputs 7-bit ASCII with bit 7 always
        // set (WozMon's own ROM bytes check for values like $9B/$DF/$8D,
        // i.e. ASCII+$80) - PA0-PA7 all being wired straight to the
        // connector (not just PA0-PA6) is what makes that bit 7 arrive at
        // all.
        Pia.PA = (byte)(ascii.Value | 0x80);

        Pia.Ca1 = false;
        Pia.Ca1 = true;
    }

    // Apple 1's character set is uppercase-only - no encoder shift level
    // for letters, so 'a'..'z' fold to 'A'..'Z'. Backspace/Delete map to
    // "_", the real Apple 1 keyboard's own rubout key (WozMon's GETLINE
    // treats it as destructive backspace); Enter/Return map to CR ($0D).
    private static byte? MapCharToAscii(char character) => character switch
    {
        >= 'a' and <= 'z' => (byte)(character - 'a' + 'A'),
        >= ' ' and <= '_' => (byte)character,
        '\b' or '\x7f' => (byte)'_',
        '\r' or '\n' => 0x0D,
        '\x1b' => 0x1B,
        _ => null,
    };
}
