using System.Threading.Tasks;
using Aemula.Emulation.Systems.AppleI;
using Hexa.NET.SDL3;

namespace Aemula.Tests.Emulation.Systems.AppleI;

public class AppleISystemCharacterMemoryTests
{
    private const ushort NextCharLoop = 0xFF29;

    // The recirculating rings advance only on MEM0-gated character-times and
    // turn over exactly once per video frame (see
    // AppleISystem.CharacterMemory.cs), so a write can take up to a full
    // frame to reach the cursor's ring position - 256 lines * 65
    // character-times * 14 master ticks.
    private const int MasterTicksPerFrame = 256 * 65 * 14;

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
        // WozMon's reset path echoes "\" then CR before it first reaches the
        // keyboard-poll loop, and its ECHO routine now genuinely blocks on
        // the display busy flag. The video counters start from zero on reset
        // (unlike real hardware, where they've been free-running since
        // power-on), so the very first echo can't commit until the ring has
        // turned over once - budget a few frames.
        for (var i = 0; i < MasterTicksPerFrame * 3; i++)
        {
            system.Tick();

            if (system.Cpu.Sync && system.Cpu.Address == NextCharLoop)
            {
                return true;
            }
        }

        return false;
    }

    // Runs until the recirculating cursor bit is at the write point and
    // returns the ring position it's sitting at - the position the next
    // committed character will land in. -1 if it never surfaces in the
    // budget (a little over one full frame-long rotation).
    private static int RunToCursorPosition(AppleISystem system)
    {
        for (var i = 0; i < MasterTicksPerFrame + MasterTicksPerFrame / 8; i++)
        {
            system.Tick();

            if (system.CursorOutForTests)
            {
                return system.RingPositionForTests;
            }
        }

        return -1;
    }

    // WozMon's GETLINE echoes every typed character back out through
    // ECHO/DSP, so pressing a key and running long enough should land that
    // same character in the character-memory ring at whatever position the
    // cursor bit is currently sitting. The write only commits on the
    // character-clock where the cursor bit passes the write point, and the
    // rings only turn over once per frame, so this budgets a few frames
    // rather than assuming the write is instant.
    [Test]
    public async Task TypedCharacterLandsInCharacterMemoryAtCursor()
    {
        var system = new AppleISystem();
        system.LoadProgram("");

        await Assert.That(RunToNextCharLoop(system)).IsTrue();

        var cursorPosition = RunToCursorPosition(system);
        await Assert.That(cursorPosition).IsGreaterThanOrEqualTo(0);

        // Nothing has been written where the cursor currently is.
        await Assert.That(system.PeekCharacterCodeForTests(cursorPosition)).IsEqualTo((byte)0);

        PressKey(system, 'A');

        var committed = false;

        for (var i = 0; i < MasterTicksPerFrame * 3 && !committed; i++)
        {
            system.Tick();
            committed = system.PeekCharacterCodeForTests(cursorPosition) != 0;
        }

        // 'A' -> uppercase ASCII 'A' | 0x80 into PA, echoed back out to
        // $D012 by WozMon with the high bit stripped by the PIA's own DDR
        // masking (only PB0-PB6 are wired as outputs) - whatever 6-of-7
        // bits made it into the character rings should be non-zero and
        // stable once committed.
        await Assert.That(committed).IsTrue();
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

        var cursorPosition = RunToCursorPosition(system);
        await Assert.That(cursorPosition).IsGreaterThanOrEqualTo(0);

        PressKey(system, 'A');

        var committed = false;
        var wasCursorOutBeforeCommit = true;

        for (var i = 0; i < MasterTicksPerFrame * 3 && !committed; i++)
        {
            wasCursorOutBeforeCommit = system.CursorOutForTests;
            system.Tick();
            committed = system.PeekCharacterCodeForTests(cursorPosition) != 0;
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

        // A ring position only recurs once per full rotation, which is now
        // one video frame (the rings are MEM0-gated, not clocked every
        // character-time) - so budget a little over a frame of master ticks.
        for (var i = 0; i < MasterTicksPerFrame + MasterTicksPerFrame / 8 && !cursorBackAtExpectedPosition; i++)
        {
            system.Tick();
            cursorBackAtExpectedPosition =
                system.RingPositionForTests == expectedRingPosition && system.CursorOutForTests;
        }

        await Assert.That(cursorBackAtExpectedPosition).IsTrue();
    }

    // WozMon's reset path unconditionally echoes "\" (the ESCAPE glyph) and
    // then a CR through ECHO/DSP before it ever reads the keyboard. ECHO
    // blocks on the display busy flag between the two, so both writes have to
    // land - and at successive cursor positions, not on top of each other.
    // The character rings can only accept a byte on the character-clock the
    // cursor bit passes the write point, so this budgets a few frames.
    [Test]
    public async Task ResetEchoLandsBackslashThenCarriageReturnAtDistinctPositions()
    {
        // Six PIA display bits (skipping PB5) fed through the write mux, so a
        // byte's stored code keeps bits 0-4 and 6. "\" is echoed as $DC,
        // masked to $5C by the display DDR -> 0x3C; CR is echoed as $8D,
        // masked to $0D -> 0x0D.
        const byte BackslashCode = 0x3C;
        const byte CarriageReturnCode = 0x0D;

        var system = new AppleISystem();
        system.LoadProgram("");

        for (var i = 0; i < MasterTicksPerFrame * 3; i++)
        {
            system.Tick();
        }

        var first = system.PeekCharacterCodeForTests(0);
        var second = system.PeekCharacterCodeForTests(1);

        await Assert.That(first).IsEqualTo(BackslashCode);
        await Assert.That(second).IsEqualTo(CarriageReturnCode);
        await Assert.That(first).IsNotEqualTo(second);
    }
}
