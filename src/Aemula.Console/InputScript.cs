using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Hexa.NET.SDL3;

namespace Aemula.Console;

// Parses the --input scripting spec and replays it against a running system,
// one batch of events per completed frame - so a headless run can be driven
// exactly the way a person would drive the UI, and the resulting frames
// captured with --screenshot / --screenshot-every.
//
// Format: a comma-separated list of items. An item is "<frame>:<action>",
// where <frame> is a completed-frame count and <action> is one of:
//
//   - a control token with a trailing '+' (press / switch closed) or '-'
//     (release / switch open). Every token name is supplied by the running
//     system, so InputScript itself knows nothing about any particular
//     machine's controls:
//       - joystick / button names from EmulatedSystem.InputKeyBindings,
//         delivered as the exact SDL key events EmulationWindow sends (the
//         Atari 2600, say, binds up/down/left/right/fire)
//       - console-panel names from the ConsoleControl.Mnemonic of each
//         EmulatedSystem.ConsoleControls entry (the Atari 2600's reset,
//         select, tv-type, left-diff, right-diff; the Apple I's reset,
//         clear-screen)
//
//   - a double-quoted string, typed one character per frame into the
//     system's keyboard through the same OnKeyEvent path the UI uses (the
//     Apple I keyboard, say). A comma inside the quotes is literal, not an
//     item separator. Recognised escapes: \n / \r -> Return (CR), \e ->
//     Escape, \t -> Tab, \\ -> backslash, \" -> quote.
//
// e.g. --input "0:reset+,4:reset-,90:right+,150:right-"
//      --input "30:\"10 PRINT HELLO\n20 GOTO 10\nRUN\n\""
public sealed class InputScript
{
    private readonly record struct ScheduledEvent(int Frame, string Token, bool Press);

    private readonly record struct TypedKey(int Frame, char Character);

    // Frames between successive typed characters. WozMon's echo can block for
    // close to a whole frame while a character commits to the display rings,
    // so one clear frame per key keeps a fast burst from overrunning the
    // keyboard strobe before the program has read it.
    private const int FramesPerTypedKey = 2;

    private readonly List<ScheduledEvent> _events;
    private readonly List<TypedKey> _typedKeys;

    private InputScript(List<ScheduledEvent> events, List<TypedKey> typedKeys)
    {
        _events = events;
        _typedKeys = typedKeys;
    }

    public static InputScript Parse(string spec, EmulatedSystem system)
    {
        var knownTokens = system.InputKeyBindings.Keys
            .Concat(system.ConsoleControls.Select(c => c.Mnemonic))
            .ToArray();
        var events = new List<ScheduledEvent>();
        var typedKeys = new List<TypedKey>();

        foreach (var rawItem in SplitItems(spec))
        {
            var item = rawItem.Trim();
            if (item.Length == 0)
            {
                continue;
            }

            var colon = item.IndexOf(':');
            if (colon <= 0 || colon == item.Length - 1)
            {
                throw new ArgumentException($"--input item '{item}' is not '<frame>:<action>'.");
            }

            if (!int.TryParse(item.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out var frame))
            {
                throw new ArgumentException($"--input item '{item}' has a non-numeric frame.");
            }

            var action = item[(colon + 1)..];

            if (action.Length >= 2 && action[0] == '"' && action[^1] == '"')
            {
                var text = Unescape(action[1..^1]);
                for (var i = 0; i < text.Length; i++)
                {
                    typedKeys.Add(new TypedKey(frame + i * FramesPerTypedKey, text[i]));
                }
                continue;
            }

            var press = action[^1] switch
            {
                '+' => true,
                '-' => false,
                _ => throw new ArgumentException($"--input action '{action}' must be a quoted string or end with '+' or '-'."),
            };

            var name = action[..^1].ToLowerInvariant();
            if (!knownTokens.Contains(name))
            {
                throw new ArgumentException(
                    $"--input token '{name}' is not one of: {string.Join(", ", knownTokens)}.");
            }

            events.Add(new ScheduledEvent(frame, name, press));
        }

        return new InputScript(events, typedKeys);
    }

    // Splits the spec on commas, except commas inside a double-quoted string
    // (which are literal characters of typed text).
    private static IEnumerable<string> SplitItems(string spec)
    {
        var start = 0;
        var inQuotes = false;

        for (var i = 0; i < spec.Length; i++)
        {
            switch (spec[i])
            {
                case '"':
                    inQuotes = !inQuotes;
                    break;
                case ',' when !inQuotes:
                    yield return spec[start..i];
                    start = i + 1;
                    break;
            }
        }

        yield return spec[start..];
    }

    private static string Unescape(string text)
    {
        if (!text.Contains('\\'))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i++;
                builder.Append(text[i] switch
                {
                    'n' or 'r' => '\r',
                    'e' => '\x1b',
                    't' => '\t',
                    var other => other,
                });
            }
            else
            {
                builder.Append(text[i]);
            }
        }

        return builder.ToString();
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

        foreach (var typed in _typedKeys)
        {
            if (typed.Frame == framesCompleted)
            {
                TypeCharacter(system, typed.Character);
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

    // Delivers one character as the key-down event a system's OnKeyEvent maps
    // to ASCII (Scancode left Unknown so the handler reads Key directly). A
    // system with no keyboard handler ignores it.
    private static void TypeCharacter(EmulatedSystem system, char character)
    {
        system.OnKeyEvent(new SDLKeyboardEvent
        {
            Type = SDLEventType.KeyDown,
            Key = character,
            Scancode = SDLScancode.Unknown,
        });
    }
}
