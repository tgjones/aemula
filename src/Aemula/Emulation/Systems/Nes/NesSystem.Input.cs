using System.Collections.Generic;

namespace Aemula.Emulation.Systems.Nes;

// Host keyboard -> the pad in controller port 1 (a standard eight-button
// controller). Arrow keys are the D-pad; X / Z are the A / B face buttons;
// Right Shift and Enter are Select and Start. Select and Start sit on the pad
// on real hardware - the NES console itself carries only POWER and RESET - so
// they arrive here as controller buttons rather than console-panel widgets.
//
// The two pads hang off the 2A03's controller ports; NesSystem.DoCpuCycle
// does the $4016/$4017 wiring. Port 2 has no host-key mapping yet, so its
// pad simply reports nothing pressed.
public sealed partial class NesSystem
{
    private readonly NesController _controller1 = new();
    private readonly NesController _controller2 = new();

    public NesController Controller1 => _controller1;
    public NesController Controller2 => _controller2;

    // The generic InputScript tokens this system's OnKeyEvent understands,
    // each mapped to the Key it matches on below.
    public override IReadOnlyDictionary<string, Key> InputKeyBindings { get; } = new Dictionary<string, Key>
    {
        ["up"] = Key.Up,
        ["down"] = Key.Down,
        ["left"] = Key.Left,
        ["right"] = Key.Right,
        ["a"] = Key.X,
        ["b"] = Key.Z,
        ["select"] = Key.RightShift,
        ["start"] = Key.Return,
    };

    public override void OnKeyEvent(KeyEvent keyEvent)
    {
        var button = keyEvent.Key switch
        {
            Key.Up => NesButton.Up,
            Key.Down => NesButton.Down,
            Key.Left => NesButton.Left,
            Key.Right => NesButton.Right,
            Key.X => NesButton.A,
            Key.Z => NesButton.B,
            Key.RightShift => NesButton.Select,
            Key.Return => NesButton.Start,
            _ => NesButton.None,
        };

        if (button == NesButton.None)
        {
            return;
        }

        if (keyEvent.IsDown)
        {
            _controller1.Buttons |= button;
        }
        else
        {
            _controller1.Buttons &= ~button;
        }
    }

    // The mainboard emits one shift-clock pulse per $4016/$4017 read (gated
    // from M2 and the read decode); the register is sampled before it, so the
    // first read after latching returns A and this advances it for the next.
    private static void PulseControllerClock(NesController controller)
    {
        controller.Clock = true;
        controller.Clock = false;
    }
}
