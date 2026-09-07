using System.Linq;
using System.Threading.Tasks;
using Aemula.Emulation.Systems;
using Aemula.Emulation.Systems.AppleI;
using Hexa.NET.SDL3;

namespace Aemula.Tests.Emulation.Systems.AppleI;

// The headless-input surface the console harness drives: the RESET and CLEAR
// SCREEN keys, which carry no ASCII code and so can't ride in through the
// keyboard's OnKeyEvent path (that path is already covered by
// AppleISystemCharacterMemoryTests).
public class AppleISystemInputTests
{
    // "\" echoed by WozMon's reset path, stored through the six-bit display
    // write mux (skipping PB5): $DC -> masked $5C -> 0x3C. Same constant the
    // character-memory tests use.
    private const byte BackslashCode = 0x3C;

    // WozMon's keyboard-poll spin ("LDA KBDCR; BPL NEXTCHAR") - reaching it
    // is the sign the reset vector ran end to end.
    private const ushort NextCharLoop = 0xFF29;

    private const int MasterTicksPerFrame = 256 * 65 * 14;

    private static ConsoleControl Control(AppleISystem system, string mnemonic) =>
        system.ConsoleControls.Single(c => c.Mnemonic == mnemonic);

    private static void RunFrames(AppleISystem system, int frames)
    {
        for (var i = 0; i < MasterTicksPerFrame * frames; i++)
        {
            system.Tick();
        }
    }

    private static bool RunUntilAtNextCharLoop(AppleISystem system, int frameBudget)
    {
        for (var i = 0; i < MasterTicksPerFrame * frameBudget; i++)
        {
            system.Tick();

            if (system.Cpu.Sync && system.Cpu.Address == NextCharLoop)
            {
                return true;
            }
        }

        return false;
    }

    private static bool BackslashOnScreen(AppleISystem system)
    {
        for (var position = 0; position < 40 * 24; position++)
        {
            if (system.PeekCharacterCodeForTests(position) == BackslashCode)
            {
                return true;
            }
        }

        return false;
    }

    // The exact shape the console harness delivers a typed character in
    // (InputScript.TypeCharacter): a key-down with the scancode left Unknown
    // so OnKeyEvent takes Key as the literal character.
    private static void TypeKey(AppleISystem system, char character) =>
        system.OnKeyEvent(new SDLKeyboardEvent
        {
            Type = SDLEventType.KeyDown,
            Key = character,
            Scancode = SDLScancode.Unknown,
        });

    [Test]
    public async Task ExposesResetAndClearScreenAsMomentaryControls()
    {
        var system = new AppleISystem();

        var controls = system.ConsoleControls;

        await Assert.That(controls.Select(c => c.Mnemonic)).IsEquivalentTo(["reset", "clear-screen"]);
        await Assert.That(controls.Select(c => c.Label)).IsEquivalentTo(["Reset", "Clear Screen"]);
        await Assert.That(controls.All(c => c.Kind == ConsoleControl.ControlKind.Momentary)).IsTrue();
        await Assert.That(controls.All(c => !c.Value)).IsTrue();
    }

    [Test]
    public async Task TypedTextEchoesIntoConsecutiveCharacterCells()
    {
        var system = new AppleISystem();
        system.LoadProgram("");

        // Reset echo "\" + CR wraps the cursor to the start of the second row
        // (ring position 40) - see AppleISystemCharacterMemoryTests.
        RunFrames(system, 3);

        // Feed "ABC" the way the harness paces it: one key every couple of
        // frames, well inside WozMon's keyboard-poll rate.
        foreach (var character in "ABC")
        {
            TypeKey(system, character);
            RunFrames(system, 2);
        }

        RunFrames(system, 3);

        // Six display bits reach the rings (PB5 skipped): $41/$42/$43 echoed
        // back by WozMon store as $21/$22/$23.
        await Assert.That(system.PeekCharacterCodeForTests(40)).IsEqualTo((byte)0x21);
        await Assert.That(system.PeekCharacterCodeForTests(41)).IsEqualTo((byte)0x22);
        await Assert.That(system.PeekCharacterCodeForTests(42)).IsEqualTo((byte)0x23);
    }

    [Test]
    public async Task ClearScreenControlBlanksTheDisplayWhileHeld()
    {
        var system = new AppleISystem();
        system.LoadProgram("");

        // Let the reset "\" + CR echo settle onto the screen.
        RunFrames(system, 3);
        await Assert.That(system.PeekCharacterCodeForTests(0)).IsEqualTo(BackslashCode);

        var clearScreen = Control(system, "clear-screen");
        clearScreen.Value = true;
        await Assert.That(clearScreen.Value).IsTrue();

        // Held for a full frame, CLR forces $00 into every column the beam
        // passes, so the whole visible field reads back blank.
        RunFrames(system, 1);
        clearScreen.Value = false;

        for (var position = 0; position < 40 * 24; position++)
        {
            await Assert.That(system.PeekCharacterCodeForTests(position)).IsEqualTo((byte)0);
        }
    }

    [Test]
    public async Task ResetControlReRunsWozMonAndBringsBackThePrompt()
    {
        var system = new AppleISystem();
        system.LoadProgram("");

        // Type a character so there's program state to tear down, then blank
        // the screen, so the restored "\" is unambiguously freshly echoed.
        await Assert.That(RunUntilAtNextCharLoop(system, 3)).IsTrue();
        TypeKey(system, 'K');
        RunFrames(system, 2);

        var clearScreen = Control(system, "clear-screen");
        clearScreen.Value = true;
        RunFrames(system, 1);
        clearScreen.Value = false;
        RunFrames(system, 1);
        await Assert.That(BackslashOnScreen(system)).IsFalse();

        var reset = Control(system, "reset");
        reset.Value = true;
        await Assert.That(reset.Value).IsTrue();
        reset.Value = false;
        await Assert.That(reset.Value).IsFalse();

        // The reset vector runs end to end back into the keyboard poll, and
        // WozMon's reset path re-echoes the "\" prompt. RESET leaves the video
        // counters free-running at whatever phase they were at, so the "\" can
        // land in any column rather than exactly column 0.
        await Assert.That(RunUntilAtNextCharLoop(system, 6)).IsTrue();
        RunFrames(system, 4);
        await Assert.That(BackslashOnScreen(system)).IsTrue();
    }
}
