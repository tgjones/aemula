namespace Aemula.Emulation.Chips.Tia;

/// <summary>
/// The TIA ball object. It is a stripped-down missile: its own copy of the
/// player LFSR counter and div-4 prescaler, a vertical-delay enable pair, a
/// width and a HMOVE latch - but no NUSIZ, so no copies and a single
/// start/reset decode point per line. The ball is simply "on" for
/// <see cref="Width"/> colour clocks from that decode point, in COLUPF,
/// sharing the playfield's priority
/// slot (order per CTRLPF D2).
///
/// Modelled as its own small type rather than folded into
/// <see cref="PlayerAndMissile"/> because a ball is not a player-and-missile
/// pair - it has no player, no graphic byte and no NUSIZ - but it mirrors the
/// missile half's shape (prescaler + counter, <see cref="UpdateDiv4"/>,
/// <see cref="DoBall"/> with the same one-colour-clock render delay).
/// </summary>
internal sealed class Ball
{
    // Registers

    // ENABL feeds two latches, mirroring the players' GRPx pair: "new" takes
    // the value ENABL D1 writes, "old" is a deferred copy the GRP1 strobe
    // clocks across (see LatchDelayedEnable) - NOT the ENABL write itself, and
    // NOT GRP0. VDELBL (VerticalDelay) is a display-time mux: the drawing path
    // reads "old" while it is set, "new" while it is clear.
    private bool _enabledNew;
    private bool _enabledOld;

    /// <summary>VDELBL D0 - when set, the drawing path uses the "old" enable
    /// latch instead of the freshly written "new" one.</summary>
    public bool VerticalDelay;

    /// <summary>
    /// Ball graphic width in colour clocks (1 / 2 / 4 / 8), from CTRLPF
    /// D4-D5 (<c>1 &lt;&lt; bits</c>).
    /// </summary>
    public byte Width = 1;

    /// <summary>
    /// HMBL - stored with bit 3 inverted, the same signed encoding the
    /// player/missile HM registers use, so it drops straight into the shared
    /// HMOVE comparator.
    /// </summary>
    public byte HorizontalMotion = 0b1000;

    // State
    public byte ClockDiv4;
    private PolynomialCounter _counter;
    public bool Reset;
    private bool _start;
    private byte _pixelsRemaining;
    private bool _drawNext;

    /// <summary>
    /// Whether the ball's pixel is lit at the current colour clock. Fed to the
    /// priority resolver in <see cref="TiaChip"/> at the playfield's slot, but
    /// always coloured from COLUPF.
    /// </summary>
    public bool PixelOn;

    /// <summary>The enable bit the drawing path samples this colour clock:
    /// the "old" latch under VDELBL, the "new" latch otherwise.</summary>
    private bool ActiveEnabled => VerticalDelay ? _enabledOld : _enabledNew;

    /// <summary>ENABL D1: write the ball's "new" enable latch.</summary>
    public void WriteEnable(bool value) => _enabledNew = value;

    /// <summary>Copy "new" into "old". Clocked by the GRP1 strobe, never by
    /// ENABL itself - that one-write lag is the point of the delay latch.</summary>
    public void LatchDelayedEnable() => _enabledOld = _enabledNew;

    /// <summary>
    /// Advances the ball's div-4 prescaler and LFSR counter - the same path
    /// <see cref="PlayerAndMissile.UpdateMissileDiv4"/> runs for a missile,
    /// minus the RESMP player lock. The single decode value below is the one
    /// the player and missile main copies also use, so the ball repeats at
    /// the same fixed 160-colour-clock line period.
    /// </summary>
    public void UpdateDiv4()
    {
        ClockDiv4++;

        if (ClockDiv4 > 3)
        {
            ClockDiv4 = 0;

            _counter.Increment();
            if (_counter.Value == 0b111111 || Reset)
            {
                Reset = false;
                _counter.Reset();
            }

            if (_counter.Value == 0b101101)
            {
                // Self-resets the counter, exactly like the player/missile
                // main copy, so the start point recurs once per line.
                Reset = true;
                _start = true;
            }
        }
    }

    /// <summary>
    /// Latches <see cref="PixelOn"/> for this colour clock. Carries the same
    /// one-colour-clock render delay the player's serial graphics shift and
    /// the missile have (<see cref="_drawNext"/> holds the bit decided last
    /// call), so the ball lines up with the other objects.
    /// </summary>
    public void DoBall()
    {
        PixelOn = _drawNext;
        _drawNext = false;

        if (_start)
        {
            _start = false;
            _pixelsRemaining = Width;
        }

        if (_pixelsRemaining > 0)
        {
            _pixelsRemaining--;
            _drawNext = ActiveEnabled;
        }
    }
}
