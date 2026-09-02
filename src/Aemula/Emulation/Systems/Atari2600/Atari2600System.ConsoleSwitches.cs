using System.Collections.Generic;

namespace Aemula.Emulation.Systems.Atari2600;

// The six-switch console panel, read by the program as SWCHB (RIOT port B).
// RESET and SELECT are momentary push buttons; the TV-type and the two
// difficulty switches latch. Every one wires straight to a PB pin with
// nothing else on the board driving those pins, so - exactly like the
// joystick pins in Atari2600System.Input.cs - the state just lives on
// _riot.PB and the UI pokes it through ConsoleControls.
public sealed partial class Atari2600System
{
    // SWCHB bit assignments (Stella Programmer's Guide). RESET/SELECT read 0
    // while pressed; TV type reads 1 for colour; a difficulty switch reads 1
    // in the A ("pro") position.
    private const byte SwchbReset = 0b0000_0001;
    private const byte SwchbSelect = 0b0000_0010;
    private const byte SwchbColor = 0b0000_1000;
    private const byte SwchbLeftDifficulty = 0b0100_0000;
    private const byte SwchbRightDifficulty = 0b1000_0000;

    private ConsoleControl[] _consoleControls = [];

    public override IReadOnlyList<ConsoleControl> ConsoleControls => _consoleControls;

    private void InitializeConsoleSwitches()
    {
        // Power-on rest state: colour TV, both difficulties at B (amateur),
        // and - crucially - RESET and SELECT released. Those two are active
        // low with a pull-up, so "not pressed" is a 1; booting them at 0 (as
        // this system did before the panel was wired up) reads as RESET held
        // forever, which pins games like Pitfall! in their startup state.
        _riot.PB = SwchbReset | SwchbSelect | SwchbColor;

        // Ordered left-to-right as they sit on the real console panel.
        _consoleControls =
        [
            new ConsoleControl(
                "TV Type",
                ConsoleControl.ControlKind.Toggle,
                () => GetSwchb(SwchbColor),
                on => SetSwchb(SwchbColor, on),
                offLabel: "B·W",
                onLabel: "Color"),

            new ConsoleControl(
                "Left Diff.",
                ConsoleControl.ControlKind.Toggle,
                () => GetSwchb(SwchbLeftDifficulty),
                on => SetSwchb(SwchbLeftDifficulty, on),
                offLabel: "B",
                onLabel: "A"),

            new ConsoleControl(
                "Right Diff.",
                ConsoleControl.ControlKind.Toggle,
                () => GetSwchb(SwchbRightDifficulty),
                on => SetSwchb(SwchbRightDifficulty, on),
                offLabel: "B",
                onLabel: "A"),

            new ConsoleControl(
                "Select",
                ConsoleControl.ControlKind.Momentary,
                () => !GetSwchb(SwchbSelect),
                held => SetSwchb(SwchbSelect, !held)),

            new ConsoleControl(
                "Reset",
                ConsoleControl.ControlKind.Momentary,
                () => !GetSwchb(SwchbReset),
                held => SetSwchb(SwchbReset, !held)),
        ];
    }

    private bool GetSwchb(byte mask) => (_riot.PB & mask) != 0;

    private void SetSwchb(byte mask, bool high)
    {
        if (high)
        {
            _riot.PB |= mask;
        }
        else
        {
            _riot.PB &= (byte)~mask;
        }
    }
}
