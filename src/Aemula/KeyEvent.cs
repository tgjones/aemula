namespace Aemula;

public readonly struct KeyEvent
{
    public required bool IsDown { get; init; }

    // Modifier-independent key identity - stable across a press even if a
    // modifier (Shift, say) is released mid-hold. See Key's remarks.
    public required Key Key { get; init; }

    // The Shift/AltGr-resolved character this key produces on the host's
    // current keyboard layout; null for a key with no text form (arrows and
    // the like). Host-layout-dependent resolution happens in EmulationWindow,
    // which still has SDL - everything below it only ever sees the resolved
    // result.
    public char? Character { get; init; }

    public bool Ctrl { get; init; }
}
