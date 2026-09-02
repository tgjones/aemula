using System.Collections.Generic;
using Aemula.Emulation.Systems;

namespace Aemula.Emulation.Systems.SpaceInvaders;

// The coin slot, the two start buttons, and the coin-info DIP switch on the
// cabinet. None of them ride a joystick port - each wires straight to an
// input-port latch bit that the ROM polls (see GetIOPort1Value /
// GetIOPort2Value) - so, like the Atari 2600's console panel, they can't come
// in through OnKeyEvent and the UI surfaces them as status-bar widgets
// instead. The players' shoot / left / right stay on the keyboard in
// OnKeyEvent.
public sealed partial class SpaceInvadersSystem
{
    private ConsoleControl[] _consoleControls = [];

    public override IReadOnlyList<ConsoleControl> ConsoleControls => _consoleControls;

    // The three buttons each read 1 on port 1 while pressed (bits 0, 2 and 1).
    private bool _keyCoin;
    private bool _key1PStart;
    private bool _key2PStart;

    // DIP switch 7, read as port 2 bit 7 - inverted there: shown = bit 0. The
    // cabinet ships with it on, so the attract screen prompts for a coin.
    private bool _coinInfoDisplayed = true;

    private void InitializeConsoleControls()
    {
        _consoleControls =
        [
            new ConsoleControl(
                "Coin Info",
                "coin-info",
                ConsoleControl.ControlKind.Toggle,
                () => _coinInfoDisplayed,
                on => _coinInfoDisplayed = on,
                offLabel: "Off",
                onLabel: "On"),

            new ConsoleControl(
                "Insert Coin",
                "coin",
                ConsoleControl.ControlKind.Momentary,
                () => _keyCoin,
                held => _keyCoin = held),

            new ConsoleControl(
                "1P Start",
                "1p-start",
                ConsoleControl.ControlKind.Momentary,
                () => _key1PStart,
                held => _key1PStart = held),

            new ConsoleControl(
                "2P Start",
                "2p-start",
                ConsoleControl.ControlKind.Momentary,
                () => _key2PStart,
                held => _key2PStart = held),
        ];
    }
}
