namespace Aemula.Emulation.Chips.Tia;

/// <summary>
/// The 15 sticky object-pair collision latches TIA exposes through the
/// CXM0P..CXPPMM read registers. Every colour clock in the active display,
/// <see cref="Accumulate"/> ORs in a bit for each object pair whose raw
/// pixels coincide there; the bits then stay set until a CXCLR write calls
/// <see cref="Clear"/>.
///
/// Detection is priority-independent - a pair still collides when one
/// object's pixel is hidden behind a higher-priority one - so this reads the
/// pre-resolver <see cref="ObjectPixels"/>, never the drawn Lum/Col output.
///
/// Backed by a single <see cref="ushort"/> bitfield with no allocation: it is
/// touched on the video hot path once per colour clock. Bit positions are
/// arbitrary (nothing external sees the raw word); the register decode in
/// <see cref="TiaChip"/> maps named bits onto D6/D7.
/// </summary>
internal struct CollisionLatches
{
    // "A_B" names the pair "object A overlapping object B". These are the 15
    // pairs TIA actually wires to a latch: every unordered pairing of
    // { P0, P1, M0, M1, PF, BL } except P0/BK-style non-pairs. See the CX
    // register table in TiaChip's read decode for how they map to D6/D7.
    public const ushort M0P0 = 1 << 0;
    public const ushort M0P1 = 1 << 1;
    public const ushort M1P0 = 1 << 2;
    public const ushort M1P1 = 1 << 3;
    public const ushort P0PF = 1 << 4;
    public const ushort P0BL = 1 << 5;
    public const ushort P1PF = 1 << 6;
    public const ushort P1BL = 1 << 7;
    public const ushort M0PF = 1 << 8;
    public const ushort M0BL = 1 << 9;
    public const ushort M1PF = 1 << 10;
    public const ushort M1BL = 1 << 11;
    public const ushort BLPF = 1 << 12;
    public const ushort P0P1 = 1 << 13;
    public const ushort M0M1 = 1 << 14;

    private ushort _bits;

    /// <summary>
    /// True if the given pair latch is currently set. <paramref name="pair"/>
    /// is one of the bit constants (or an OR of several, in which case this is
    /// "any of them").
    /// </summary>
    public readonly bool IsSet(ushort pair) => (_bits & pair) != 0;

    /// <summary>
    /// OR in every pair that overlaps at this colour clock. Call once per
    /// colour clock in the active display, with the pre-resolver presence
    /// bits for that clock.
    /// </summary>
    public void Accumulate(in ObjectPixels p)
    {
        ushort hits = 0;

        if (p.Missile0 && p.Player0) hits |= M0P0;
        if (p.Missile0 && p.Player1) hits |= M0P1;
        if (p.Missile1 && p.Player0) hits |= M1P0;
        if (p.Missile1 && p.Player1) hits |= M1P1;
        if (p.Player0 && p.Playfield) hits |= P0PF;
        if (p.Player0 && p.Ball) hits |= P0BL;
        if (p.Player1 && p.Playfield) hits |= P1PF;
        if (p.Player1 && p.Ball) hits |= P1BL;
        if (p.Missile0 && p.Playfield) hits |= M0PF;
        if (p.Missile0 && p.Ball) hits |= M0BL;
        if (p.Missile1 && p.Playfield) hits |= M1PF;
        if (p.Missile1 && p.Ball) hits |= M1BL;
        if (p.Ball && p.Playfield) hits |= BLPF;
        if (p.Player0 && p.Player1) hits |= P0P1;
        if (p.Missile0 && p.Missile1) hits |= M0M1;

        _bits |= hits;
    }

    /// <summary>CXCLR - drop all 15 latches back to 0.</summary>
    public void Clear() => _bits = 0;
}
