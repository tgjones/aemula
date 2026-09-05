using System;

namespace Aemula.Emulation.Chips;

/// <summary>
/// 1024-bit, single-bit-wide dynamic PMOS recirculating shift register.
/// There is no address bus - the only way in or out is <see cref="In"/> and
/// <see cref="Out"/>, and the only way to see a given bit again is to wait
/// the full 1024 clocks for it to come back around. Real hardware needs a
/// non-overlapping two-phase clock to keep the dynamic (capacitor-storage)
/// stages refreshed; that timing detail isn't modelled; a bit is shifted the
/// instant <see cref="Phi2"/> rises, which is functionally equivalent for
/// anything that (like this codebase) never inspects the register mid-cycle.
/// </summary>
public sealed class Signetics2504Chip
{
    private const int Length = 1024;

    private readonly bool[] _bits = new bool[Length];

    public bool In { private get; set; }

    public bool Out => _bits[Length - 1];

    public bool Phi1 { private get; set; }

    private bool _phi2;

    public bool Phi2
    {
        get => _phi2;
        set
        {
            var risingEdge = value && !_phi2;
            _phi2 = value;

            if (!risingEdge)
            {
                return;
            }

            Array.Copy(_bits, 0, _bits, 1, Length - 1);
            _bits[0] = In;
        }
    }

    // Real hardware needs a reset/power-on path that seeds exactly one '1'
    // bit somewhere in the cursor ring (see AppleISystem.CharacterMemory.cs)
    // - a pure recirculating register has no other way to ever contain a
    // bit that wasn't shifted in through In. Not used for anything else;
    // the write side never pokes an arbitrary position mid-recirculation.
    internal void Poke(int ringPosition, bool value) => _bits[ringPosition] = value;

    internal void Clear() => Array.Clear(_bits);

    // Direct access into the ring, used only by AppleISystem's video-draw
    // code as a tractable stand-in for literally re-deriving a character's
    // position by counting recirculation cycles from the read side (which
    // the real hardware's 2519 line buffer exists to do, and which this
    // codebase doesn't reproduce cycle-for-cycle - see
    // AppleISystem.CharacterMemory.cs). The write side never uses this; it
    // only ever goes through In/Out, exactly like the real chip.
    internal bool Peek(int ringPosition) => _bits[ringPosition];
}
