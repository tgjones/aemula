using System.Collections.Generic;

namespace Aemula.Emulation.Systems.AppleI;

// Headless-input surface for the console harness (Aemula.Console --input).
// Typed characters ride in through OnKeyEvent (AppleISystem.Keyboard.cs) - the
// same entry point the UI uses - so nothing extra is needed for the keyboard
// itself. The two keys on the B4 connector that carry no ASCII code, RESET
// and CLEAR SCREEN (B4.12), can't travel that path, so they're surfaced here
// as console controls, the same mechanism the Atari 2600's panel switches use.
public sealed partial class AppleISystem
{
    // True while the RESET key is physically held. The key grounds the 6502
    // and PIA reset lines; WozMon's reset routine (which leaves the "\"
    // prompt) runs once it's released.
    private bool _resetKeyHeld;

    private ConsoleControl[] _consoleControls = [];

    public override IReadOnlyList<ConsoleControl> ConsoleControls => _consoleControls;

    private void InitializeConsoleControls()
    {
        _consoleControls =
        [
            new ConsoleControl(
                "Reset",
                "reset",
                ConsoleControl.ControlKind.Momentary,
                () => _resetKeyHeld,
                HoldReset),

            new ConsoleControl(
                "Clear Screen",
                "clear-screen",
                ConsoleControl.ControlKind.Momentary,
                () => _clearScreenKeyDown,
                held => _clearScreenKeyDown = held),
        ];
    }

    private void HoldReset(bool held)
    {
        _resetKeyHeld = held;

        if (held)
        {
            // Grounds RES on both chips for as long as the key is down (the
            // 6502 freezes while its RES pin is low).
            Cpu.Res = false;
            Pia.Res = false;
        }
        else
        {
            // Release: WozMon's reset routine runs and re-echoes the "\"
            // prompt. Routed through Reset() - the same path as the UI's Reset
            // command - which, like the real key, leaves the screen contents
            // and free-running video counters alone (see Reset()).
            Reset();
        }
    }
}
