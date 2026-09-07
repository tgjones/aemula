using System;

namespace Aemula.Emulation.Chips;

/// <summary>
/// 1024-bit, single-bit-wide dynamic PMOS recirculating shift register.
/// There is no address bus - the only way in or out is <see cref="In"/> and
/// <see cref="Out"/>, and the only way to see a given bit again is to wait
/// the full 1024 clocks for it to come back around. Real hardware needs a
/// non-overlapping two-phase clock to keep the dynamic (capacitor-storage)
/// stages refreshed; that timing detail isn't modelled beyond what a stage
/// observably does with one clock pulse: <see cref="In"/> is taken as the
/// pulse opens (the phase's falling edge - these are negative-going MOS
/// clocks) and the register has advanced one position, with the new bit at
/// <see cref="Out"/>, once the pulse closes (the rising edge). Either phase
/// does this. The Apple I alternates the two on successive character-times
/// and needs a new bit on every one of them (40 distinct characters per
/// row, 1024 shifts per frame - see AppleISystem.CharacterMemory.cs), so a
/// position per phase pulse is what its observable behaviour requires;
/// whether that is one half-stage of two per bit, or something else inside
/// the part, isn't observable and isn't modelled.
/// </summary>
public sealed class Signetics2504Chip
{
    private const int Length = 1024;

    // The 1024 stages, kept as a circular buffer: a shift moves the head
    // rather than every bit (the Apple I shifts seven of these 1024 times a
    // frame, and moving a kilobyte each time was most of the emulator's
    // whole tick cost). Stage i - 0 being the one In feeds, Length - 1 the
    // one Out reads - lives at _bits[(_head + i) & (Length - 1)].
    private readonly bool[] _bits = new bool[Length];
    private int _head;

    public bool In { private get; set; }

    public bool Out => _bits[(_head + Length - 1) & (Length - 1)];

    private bool _sampledIn;

    private bool _phi1 = true;

    public bool Phi1
    {
        get => _phi1;
        set
        {
            var fallingEdge = _phi1 && !value;
            var risingEdge = value && !_phi1;
            _phi1 = value;

            if (fallingEdge)
            {
                _sampledIn = In;
            }
            else if (risingEdge)
            {
                Shift();
            }
        }
    }

    private bool _phi2 = true;

    public bool Phi2
    {
        get => _phi2;
        set
        {
            var fallingEdge = _phi2 && !value;
            var risingEdge = value && !_phi2;
            _phi2 = value;

            if (fallingEdge)
            {
                _sampledIn = In;
            }
            else if (risingEdge)
            {
                Shift();
            }
        }
    }

    // Advancing every stage by one is the same as stepping the head back by
    // one and writing the new stage 0.
    private void Shift()
    {
        _head = (_head + Length - 1) & (Length - 1);
        _bits[_head] = _sampledIn;
    }

    // Real hardware needs a reset/power-on path that seeds the cursor ring
    // (see AppleISystem.CharacterMemory.cs) - a pure recirculating register
    // has no other way to ever contain a bit that wasn't shifted in through
    // In. The cursor marker is a single 0 in a field of 1s, so that seed is
    // Fill() followed by one Poke(pos, false). Not used for anything else;
    // the write side never pokes an arbitrary position mid-recirculation.
    internal void Poke(int ringPosition, bool value) => _bits[(_head + ringPosition) & (Length - 1)] = value;

    internal void Clear() => Array.Clear(_bits);

    internal void Fill() => Array.Fill(_bits, true);

    // Test-only introspection - kept for AppleISystemCharacterMemoryTests,
    // which needs to inspect a specific ring position directly to confirm
    // the write side landed the right bits. The write side itself never
    // calls this; it only ever goes through In/Out, exactly like the real
    // chip.
    internal bool Peek(int ringPosition) => _bits[(_head + ringPosition) & (Length - 1)];
}
