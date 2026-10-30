using System.Collections.Generic;

namespace Aemula.Emulation.Systems.BbcMicro;

// The BREAK key and the keyboard's indicator LEDs. BREAK is not in the key
// matrix: it is a switch that fires the reset button's 555 (IC16).
public sealed partial class BbcMicroSystem
{
    private bool _breakHeld;

    private ConsoleControl[]? _consoleControls;

    public override IReadOnlyList<ConsoleControl> ConsoleControls => _consoleControls ??=
    [
        new ConsoleControl(
            "Break",
            "break",
            ConsoleControl.ControlKind.Momentary,
            () => _breakHeld,
            held =>
            {
                if (held && !_breakHeld)
                {
                    Reset();
                }

                _breakHeld = held;
            }),

        ConsoleControl.CreateReadout("Caps Lock", "caps-lock-led", () => CapsLockLed ? "On" : "Off"),
        ConsoleControl.CreateReadout("Shift Lock", "shift-lock-led", () => ShiftLockLed ? "On" : "Off"),
    ];

    // Addressable latch outputs Q6 and Q7 drive the LEDs through a transistor
    // each, so a LED is lit while its latch bit is low.
    public bool CapsLockLed => !_ic32.Q6;

    public bool ShiftLockLed => !_ic32.Q7;
}
