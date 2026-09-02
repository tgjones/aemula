using System;

namespace Aemula.Emulation.Systems;

// A switch or button on a system's physical console housing - the Atari
// 2600's RESET / SELECT / difficulty / colour switches, a home computer's
// BREAK key, and so on. These are operated by hand, with no path through the
// emulated keyboard or joystick ports, so they can't ride in on OnKeyEvent;
// the UI renders each system's list as a row of widgets in its status bar.
public sealed class ConsoleControl
{
    public enum ControlKind
    {
        // A push button, closed only while the user actively holds it (RESET,
        // SELECT). The UI drives Value true for exactly as long as it's held.
        Momentary,

        // A two-position latching switch (colour / black-and-white, difficulty
        // A / B). Value is the current position and persists until changed.
        Toggle,
    }

    private readonly Func<bool> _get;
    private readonly Action<bool> _set;

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

    public string Label { get; }

    // A short, lower-case, hyphenated name for this control ("reset",
    // "left-diff") - stable and terminal-friendly, for scripting the panel
    // from outside the UI (the headless runner's --input). The display Label
    // is free to change wording; this is the identifier.
    public string Mnemonic { get; }

    public ControlKind Kind { get; }

    // For a Toggle, the caption to show for the false / true position (e.g.
    // "B·W" / "Color"). Null for a Momentary control.
    public string? OffLabel { get; }
    public string? OnLabel { get; }

    // Momentary: true while the button is held. Toggle: the current position.
    public bool Value
    {
        get => _get();
        set => _set(value);
    }
}
