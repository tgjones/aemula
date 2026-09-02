using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Hexa.NET.SDL3;

namespace Aemula.Console;

// Parses the --input scripting spec and replays it against a running system,
// one batch of events per completed frame - so a headless run can be driven
// exactly the way a person would drive the UI, and the resulting frames
// captured with --screenshot / --screenshot-every.
//
// Format: a comma-separated list of "<frame>:<token>" items. <frame> is a
// completed-frame count; <token> is a control name with a trailing '+'
// (press / switch closed) or '-' (release / switch open). Every token name is
// supplied by the running system, so InputScript itself knows nothing about
// any particular machine's controls:
//
//   - joystick / button names from EmulatedSystem.InputKeyBindings, delivered
//     as the exact SDL key events EmulationWindow sends (the Atari 2600, say,
//     binds up/down/left/right/fire)
//   - console-panel names from the ConsoleControl.Mnemonic of each
//     EmulatedSystem.ConsoleControls entry (the Atari 2600's reset, select,
//     tv-type, left-diff, right-diff)
//
// e.g. --input "0:reset+,4:reset-,90:right+,150:right-"
public sealed class InputScript
{
    private readonly record struct ScheduledEvent(int Frame, string Token, bool Press);

    private readonly List<ScheduledEvent> _events;

    private InputScript(List<ScheduledEvent> events) => _events = events;

    public static InputScript Parse(string spec, EmulatedSystem system)
    {
        var knownTokens = system.InputKeyBindings.Keys
            .Concat(system.ConsoleControls.Select(c => c.Mnemonic))
            .ToArray();
        var events = new List<ScheduledEvent>();

        foreach (var item in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = item.IndexOf(':');
            if (colon <= 0 || colon == item.Length - 1)
            {
                throw new ArgumentException($"--input item '{item}' is not '<frame>:<token>'.");
            }

            if (!int.TryParse(item.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out var frame))
            {
                throw new ArgumentException($"--input item '{item}' has a non-numeric frame.");
            }

            var token = item[(colon + 1)..];
            var press = token[^1] switch
            {
                '+' => true,
                '-' => false,
                _ => throw new ArgumentException($"--input token '{token}' must end with '+' or '-'."),
            };

            var name = token[..^1].ToLowerInvariant();
            if (!knownTokens.Contains(name))
            {
                throw new ArgumentException(
                    $"--input token '{name}' is not one of: {string.Join(", ", knownTokens)}.");
            }

            events.Add(new ScheduledEvent(frame, name, press));
        }

        return new InputScript(events);
    }

    // Applies every event scheduled for exactly this completed-frame count.
    public void ApplyForFrame(EmulatedSystem system, int framesCompleted)
    {
        foreach (var scheduled in _events)
        {
            if (scheduled.Frame == framesCompleted)
            {
                Apply(system, scheduled.Token, scheduled.Press);
            }
        }
    }

    private static void Apply(EmulatedSystem system, string token, bool press)
    {
        var control = system.ConsoleControls.FirstOrDefault(c => c.Mnemonic == token);
        if (control != null)
        {
            // A Toggle latches to press; a Momentary button is closed only
            // while press is true. ConsoleControl.Value handles both.
            control.Value = press;
            return;
        }

        // Otherwise it's a joystick / button token: ride it in as the exact
        // SDL key event EmulationWindow sends, using the keycode this system
        // binds the token to (see EmulatedSystem.InputKeyBindings). Parse has
        // already checked the token is one of the two known sets.
        system.OnKeyEvent(new SDLKeyboardEvent
        {
            Type = press ? SDLEventType.KeyDown : SDLEventType.KeyUp,
            Key = system.InputKeyBindings[token],
        });
    }
}
