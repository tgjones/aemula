using System;

namespace Aemula.Emulation.Systems;

// A switch, button or indicator on a system's physical console housing - the
// Atari 2600's RESET / SELECT / difficulty / colour switches, a home
// computer's BREAK key, a cassette deck's transport buttons and counter, and
// so on. These are operated by hand, with no path through the emulated
// keyboard or joystick ports, so they can't ride in on OnKeyEvent; the UI
// renders each system's list as a row of widgets in its status bar.
public sealed class ConsoleControl
{
    public enum ControlKind
    {
        // A push button, closed only while the user actively holds it (RESET,
        // SELECT). Value is written true on press and false on release, and
        // not in between, so a setter may act on either edge.
        Momentary,

        // A two-position latching switch (colour / black-and-white, difficulty
        // A / B). Value is the current position and persists until changed.
        Toggle,

        // A single button that latches: each click flips Value, and the button
        // shows a pressed/active state while Value is true (a cassette deck's
        // PLAY). OffLabel/OnLabel, when given, are the captions for the two
        // states (e.g. "Play" / "Stop").
        Latching,

        // A read-only text indicator - no input, just <see cref="Text"/> shown
        // in the bar (a tape counter). Value is unused.
        Readout,
    }

    private readonly Func<bool> _get;
    private readonly Action<bool> _set;
    private readonly Func<string>? _text;

    public ConsoleControl(
        string label,
        string mnemonic,
        ControlKind kind,
        Func<bool> get,
        Action<bool> set,
        string? offLabel = null,
        string? onLabel = null)
    {
        Label = label;
        Mnemonic = mnemonic;
        Kind = kind;
        _get = get;
        _set = set;
        OffLabel = offLabel;
        OnLabel = onLabel;
    }

    private ConsoleControl(string label, string mnemonic, Func<string> text)
    {
        Label = label;
        Mnemonic = mnemonic;
        Kind = ControlKind.Readout;
        _get = static () => false;
        _set = static _ => { };
        _text = text;
    }

    /// <summary>A read-only text indicator, e.g. a tape counter.</summary>
    public static ConsoleControl CreateReadout(string label, string mnemonic, Func<string> text) =>
        new(label, mnemonic, text);

    public string Label { get; }

    // A short, lower-case, hyphenated name for this control ("reset",
    // "left-diff") - stable and terminal-friendly, for scripting the panel
    // from outside the UI (the headless runner's --input). The display Label
    // is free to change wording; this is the identifier.
    public string Mnemonic { get; }

    public ControlKind Kind { get; }

    // For a Toggle, the caption to show for the false / true position (e.g.
    // "B·W" / "Color"). For a Latching button, the caption in the released /
    // pressed state. Null otherwise.
    public string? OffLabel { get; }
    public string? OnLabel { get; }

    // Momentary: true while the button is held. Toggle / Latching: the current
    // position. Unused for a Readout.
    public bool Value
    {
        get => _get();
        set => _set(value);
    }

    // Readout only: the text to display. Null for every other kind.
    public string? Text => _text?.Invoke();
}
