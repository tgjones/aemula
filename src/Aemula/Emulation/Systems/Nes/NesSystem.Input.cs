using System.Collections.Generic;
using Hexa.NET.SDL3;

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

    // SDL keycodes (SDLK_*). The arrow keys are scancode-derived
    // (SDL_SCANCODE_MASK | scancode); the rest are plain ASCII. Spelt out as
    // literals to match the other systems (see Atari2600System.Input.cs).
    private const int SdlkReturn = 0x0D;
    private const int SdlkX = 0x78;
    private const int SdlkZ = 0x7A;
    private const int SdlkRight = 0x4000004F;
    private const int SdlkLeft = 0x40000050;
    private const int SdlkDown = 0x40000051;
    private const int SdlkUp = 0x40000052;
    private const int SdlkRShift = 0x400000E5;

    // The generic InputScript tokens this system's OnKeyEvent understands,
    // each mapped to the SDL keycode it matches on below.
    public override IReadOnlyDictionary<string, int> InputKeyBindings { get; } = new Dictionary<string, int>
    {
        ["up"] = SdlkUp,
        ["down"] = SdlkDown,
        ["left"] = SdlkLeft,
        ["right"] = SdlkRight,
        ["a"] = SdlkX,
        ["b"] = SdlkZ,
        ["select"] = SdlkRShift,
        ["start"] = SdlkReturn,
    };

    public override void OnKeyEvent(SDLKeyboardEvent keyEvent)
    {
        var button = keyEvent.Key switch
        {
            SdlkUp => NesButton.Up,
            SdlkDown => NesButton.Down,
            SdlkLeft => NesButton.Left,
            SdlkRight => NesButton.Right,
            SdlkX => NesButton.A,
            SdlkZ => NesButton.B,
            SdlkRShift => NesButton.Select,
            SdlkReturn => NesButton.Start,
            _ => NesButton.None,
        };

        if (button == NesButton.None)
        {
            return;
        }

        if (keyEvent.Type == SDLEventType.KeyDown)
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
