using System.Threading.Tasks;
using Aemula.Emulation.Systems.AppleI;
using Hexa.NET.SDL3;

namespace Aemula.Tests.Emulation.Systems.AppleI;

public class AppleISystemCharacterMemoryTests
{
    private const ushort NextCharLoop = 0xFF29;

    private static void PressKey(AppleISystem system, char key)
    {
        system.OnKeyEvent(new SDLKeyboardEvent
        {
            Type = SDLEventType.KeyDown,
            Key = key,
            Scancode = SDLScancode.Unknown,
        });
    }

    private static bool RunToNextCharLoop(AppleISystem system)
    {
        for (var i = 0; i < 10_000; i++)
        {
            system.Tick();

            if (system.Cpu.Sync && system.Cpu.Address == NextCharLoop)
            {
                return true;
            }
        }

        return false;
    }

    // WozMon's GETLINE echoes every typed character back out through
    // ECHO/DSP, so pressing a key and running long enough should land that
    // same character in the character-memory ring at the cursor's starting
    // position (row 0, column 0 - see
    // AppleISystem.CharacterMemory.cs.SeedCursor). The real hardware's
    // write can take up to just over one full 1024-cycle recirculation to
    // land, since it only commits on the specific character-clock where the
    // cursor bit happens to be passing the write point - this test budgets
    // for that worst case rather than assuming the write is instant.
    [Test]
    public async Task TypedCharacterLandsInCharacterMemoryAtCursor()
    {
        var system = new AppleISystem();
        system.LoadProgram("");

        await Assert.That(RunToNextCharLoop(system)).IsTrue();

        PressKey(system, 'A');

        var committed = false;

        for (var i = 0; i < 60_000 && !committed; i++)
        {
            system.Tick();
            committed = system.PeekCharacterCodeForTests(0) != 0;
        }

        await Assert.That(committed).IsTrue();

        // 'A' -> uppercase ASCII 'A' | 0x80 into PA, echoed back out to
        // $D012 by WozMon with the high bit stripped by the PIA's own DDR
        // masking (only PB0-PB6 are wired as outputs) - whatever 6-of-7
        // bits made it into the character rings should be non-zero and
        // stable once committed.
        var code = system.PeekCharacterCodeForTests(0);
        await Assert.That(code).IsNotEqualTo((byte)0);
    }

    // Confirmed from the schematic (see AppleISystem.CharacterMemory.cs's
    // header): the cursor bit clears at the write position on the commit
    // cycle and is re-set one character-clock later, one position further
    // on - i.e. after one committed write, CURS should next be found at
    // ring position 1, not 0.
    [Test]
    public async Task CursorAdvancesByOneAfterACommittedWrite()
    {
        var system = new AppleISystem();
        system.LoadProgram("");

        await Assert.That(RunToNextCharLoop(system)).IsTrue();

        PressKey(system, 'A');

        var committed = false;
        var wasCursorOutBeforeCommit = true;

        for (var i = 0; i < 60_000 && !committed; i++)
        {
            wasCursorOutBeforeCommit = system.CursorOutForTests;
            system.Tick();
            committed = system.PeekCharacterCodeForTests(0) != 0;
        }

        await Assert.That(committed).IsTrue();

        // The commit cycle itself must have found the cursor bit set (that's
        // what CURS true is for) and cleared it as a side effect.
        await Assert.That(wasCursorOutBeforeCommit).IsTrue();
        await Assert.That(system.CursorOutForTests).IsFalse();

        // The ring position right after the commit tick is where the write
        // landed - one character-clock later the cursor bit was re-set one
        // position further on (see the file header), so it should next
        // surface at Out exactly one full 1024-cycle rotation after this
        // same ring position comes around again.
        var expectedRingPosition = system.RingPositionForTests;
        var cursorBackAtExpectedPosition = false;

        // A ring position only recurs once every 1024 character-times, and
        // Tick() advances one master tick (1/14 of a character-time) at a
        // time - so this needs a full rotation's worth of master ticks, not
        // master ticks equal to the ring length.
        for (var i = 0; i < 1024 * 14 + 1000 && !cursorBackAtExpectedPosition; i++)
        {
            system.Tick();
            cursorBackAtExpectedPosition =
                system.RingPositionForTests == expectedRingPosition && system.CursorOutForTests;
        }

        await Assert.That(cursorBackAtExpectedPosition).IsTrue();
    }
}
