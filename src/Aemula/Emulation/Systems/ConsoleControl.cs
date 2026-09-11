using System;

namespace Aemula.Emulation.Systems;

// A switch, button, indicator or media receptacle on a system's physical
// console housing - the Atari 2600's RESET / SELECT / difficulty / colour
// switches, a home computer's BREAK key, a cassette deck's transport buttons
// and counter, the cartridge slot itself, and so on. These are operated by
// hand, with no path through the emulated keyboard or joystick ports, so they
// can't ride in on OnKeyEvent; the UI renders each system's list as a row of
// widgets in its status bar.
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

        // A removable-media receptacle: a cartridge slot, a cassette deck's
        // tape bay. <see cref="Bay"/> is the receptacle's capability data;
        // <see cref="Text"/> is the loaded image's name, or null when empty.
        // The control carries the insert / eject behaviour itself (see
        // <see cref="CreateMediaBay"/>), so there is no separate media-bay
        // interface on the system - the slot lives in the panel list like any
        // other control, positioned wherever the hardware put it.
        MediaBay,
    }

    private readonly Func<bool> _get;
    private readonly Action<bool> _set;
    private readonly Func<string?>? _text;

    private readonly Action<MediaImage>? _insert;
    private readonly Action? _eject;
    private string? _loadedName;

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

    private ConsoleControl(MediaBay bay, Action<MediaImage> insert, Action eject)
    {
        Label = bay.DisplayName;
        Mnemonic = bay.Id;
        Kind = ControlKind.MediaBay;
        Bay = bay;
        _get = static () => false;
        _set = static _ => { };
        _insert = insert;
        _eject = eject;
    }

    /// <summary>A read-only text indicator, e.g. a tape counter.</summary>
    public static ConsoleControl CreateReadout(string label, string mnemonic, Func<string> text) =>
        new(label, mnemonic, text);

    /// <summary>
    /// A removable-media receptacle. <paramref name="insert"/> threads a picked
    /// image onto the live hardware (parse the bytes, seat the cartridge);
    /// <paramref name="eject"/> clears it. The control tracks the loaded image's
    /// name for its own caption - the system never has to keep that itself.
    /// </summary>
    public static ConsoleControl CreateMediaBay(MediaBay bay, Action<MediaImage> insert, Action eject) =>
        new(bay, insert, eject);

    public string Label { get; }

    // A short, lower-case, hyphenated name for this control ("reset",
    // "left-diff") - stable and terminal-friendly, for scripting the panel
    // from outside the UI (the headless runner's --input). The display Label
    // is free to change wording; this is the identifier. For a MediaBay this
    // is the bay id ("cartridge").
    public string Mnemonic { get; }

    public ControlKind Kind { get; }

    // For a Toggle, the caption to show for the false / true position (e.g.
    // "B·W" / "Color"). For a Latching button, the caption in the released /
    // pressed state. Null otherwise.
    public string? OffLabel { get; }
    public string? OnLabel { get; }

    // MediaBay only: the receptacle's capability data (id, display name, file
    // filters). Null for every other kind.
    public MediaBay? Bay { get; }

    // Momentary: true while the button is held. Toggle / Latching: the current
    // position. Unused for a Readout or a MediaBay.
    public bool Value
    {
        get => _get();
        set => _set(value);
    }

    // Readout: the text to display. MediaBay: the loaded image's name, or null
    // when the bay is empty. Null for every other kind.
    public string? Text => Kind == ControlKind.MediaBay ? _loadedName : _text?.Invoke();

    // MediaBay only: whether the bay currently holds an image.
    public bool HasMedia => _loadedName != null;

    /// <summary>
    /// MediaBay only. Seats <paramref name="image"/> on the hardware and adopts
    /// its name for the caption. A throwing insert leaves the caption unchanged.
    /// </summary>
    public void InsertMedia(MediaImage image)
    {
        _insert!(image);
        _loadedName = image.Name;
    }

    /// <summary>MediaBay only. Clears the bay and its caption.</summary>
    public void EjectMedia()
    {
        _eject!();
        _loadedName = null;
    }
}
