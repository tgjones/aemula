using static Aemula.BitUtility;
using static Aemula.Emulation.Chips.Tia.TiaUtility;

namespace Aemula.Emulation.Chips.Tia;

internal sealed class PlayerAndMissile
{
    // Registers

    // A GRPx write feeds two latches, not one: "new" takes the value the
    // matching GRPx strobe writes, "old" is a deferred copy that the *other*
    // player's GRPx strobe clocks across (see LatchDelayedGraphics). VDELPx
    // (VerticalDelay) is a display-time mux - the drawing path reads "old"
    // while it is set, "new" while it is clear. This is how a two-line-kernel
    // sprite writes each player on alternate scanlines without tearing: the
    // half that is not being written this line keeps showing its "old" copy.
    private byte _graphicsNew;
    private byte _graphicsOld;

    /// <summary>VDELPx D0 - when set, the drawing path uses the "old"
    /// graphics latch instead of the freshly written "new" one.</summary>
    public bool VerticalDelay;

    public byte Color;
    public byte Luminance;
    public byte NumberSizePlayer;
    public byte NumberSizeMissile;
    public bool Reflect;
    public byte HorizontalMotionPlayer = 0b1000; // Stored with bit 3 inverted
    public byte HorizontalMotionMissile = 0b1000; // Stored with bit 3 inverted, like HorizontalMotionPlayer

    /// <summary>ENAM D1 - whether the missile graphic is enabled.</summary>
    public bool MissileEnabled;

    /// <summary>
    /// RESMP D1 - lock the missile onto its player. While set, the missile
    /// counter is slaved to the player counter and the missile pixel is
    /// forced dark. Releasing the lock leaves the missile aligned to the
    /// player copy start - an approximation of the real chip's "centred on
    /// the player", which offsets by a NUSIZ-size-dependent amount.
    /// </summary>
    public bool MissileLockedToPlayer;

    /// <summary>
    /// Missile graphic width in colour clocks (1 / 2 / 4 / 8), from NUSIZ
    /// D4-D5. <see cref="NumberSizeMissile"/> holds NUSIZ D3-D5, so the width
    /// selector is its bits 1-2.
    /// </summary>
    public byte MissileWidth => (byte)(1 << ((NumberSizeMissile >> 1) & 0b11));

    // Colour clocks between a copy-start decode on the position counter and
    // the copy's first drawn pixel. The position counter runs at CLK/4 and its
    // decode fires on a 4-colour-clock boundary, but the graphics scan does
    // not begin on that same clock - real TIA walks an internal start counter
    // for a few colour clocks first (Stella models this as Player/Missile
    // renderCounterOffset, a negative render-counter preroll). Without it the
    // scan starts too early relative to a graphics register written just ahead
    // of the copy: Pitfall's streaming 6-digit score kernel leaves ~1 colour
    // clock of slack on its last digit, and a too-early scan clipped that
    // digit's leading column. 2 is the smallest preroll that renders that
    // kernel's tightest digit whole against this codebase's existing
    // RESx/HMOVE position calibration.
    private const int GraphicsStartDelay = 2;

    // State
    public byte PlayerClockDiv4;
    private int _pendingStart;
    private PolynomialCounter _counter;
    public bool Reset;
    private bool _draw;
    private byte _graphicsDelay;
    private byte _scanCounter;

    // Counts colour clocks since the graphic scan last advanced. NUSIZ
    // double/quad width stretches the single player copy by walking the scan
    // slower (see StretchFactor); it does not add copies and does not touch
    // the position counter or copy decode.
    private byte _scanCounterDivider;

    /// <summary>
    /// Colour clocks each of the 8 graphic bits occupies while the player
    /// draws. NUSIZ 5 (double width) holds each bit for 2 colour clocks and
    /// NUSIZ 7 (quad width) for 4, stretching the one copy to 16 / 32 pixels;
    /// every other NUSIZ value scans one bit per colour clock. Matches Stella's
    /// Player myDivider (1 / 2 / 4), which likewise gates only the graphic scan.
    /// </summary>
    private int StretchFactor => NumberSizePlayer switch
    {
        0b101 => 2,
        0b111 => 4,
        _ => 1,
    };

    // Missile state - a missile is a degenerate player: its own copy of the
    // player's LFSR counter and div-4 prescaler, but no 8-bit graphic. It is
    // simply "on" for MissileWidth colour clocks from each copy's start.
    public byte MissileClockDiv4;
    private PolynomialCounter _missileCounter;
    public bool MissileReset;
    private int _missilePendingStart;
    private byte _missilePixelsRemaining;
    private bool _missileDrawNext;

    /// <summary>
    /// Whether the missile's pixel is lit at the current colour clock. Fed to
    /// the priority resolver in <see cref="TiaChip"/> at the same slot and
    /// colour as this object's player - see <see cref="PixelOn"/>.
    /// </summary>
    public bool MissilePixelOn;

    /// <summary>
    /// Whether this player's graphic has a lit pixel at the current colour
    /// clock. Updated by <see cref="DoPlayer"/> each colour clock.
    ///
    /// The player reports this bit rather than writing the video output
    /// itself: TIA's real priority encoder has to decide P0 vs P1 vs
    /// playfield vs background (P0 must win over P1) in a single stage, so a
    /// lone resolver in <see cref="TiaChip"/> reads every object's presence
    /// bit and picks one winner, instead of each object overwriting
    /// <see cref="TiaChip.Lum"/>/<see cref="TiaChip.Col"/> in turn.
    /// </summary>
    public bool PixelOn;

    /// <summary>The graphics byte the drawing path samples this colour clock:
    /// the "old" latch under VDELPx, the "new" latch otherwise.</summary>
    private byte ActiveGraphics => VerticalDelay ? _graphicsOld : _graphicsNew;

    /// <summary>This player's "new" graphics latch - for debug read-back only;
    /// the drawing path goes through <see cref="ActiveGraphics"/>.</summary>
    public byte GraphicsNew => _graphicsNew;

    /// <summary>GRPx strobe: write this player's own "new" graphics latch.</summary>
    public void WriteGraphics(byte value) => _graphicsNew = value;

    /// <summary>Copy "new" into "old". Clocked by the *other* player's GRPx
    /// strobe, never by this player's own - that one-write lag is the whole
    /// point of the vertical-delay latch.</summary>
    public void LatchDelayedGraphics() => _graphicsOld = _graphicsNew;

    public void UpdatePlayerDiv4()
    {
        PlayerClockDiv4++;

        if (PlayerClockDiv4 > 3)
        {
            PlayerClockDiv4 = 0;

            _counter.Increment();
            if (_counter.Value == 0b111111 || Reset)
            {
                Reset = false;
                _counter.Reset();
            }

            ExecutePlayerLogic();
        }
    }

    private void ExecutePlayerLogic()
    {
        switch (_counter.Value)
        {
            case 0b111000 when NumberSizePlayer == 0b001 || NumberSizePlayer == 0b011:
            case 0b101111 when NumberSizePlayer == 0b011 || NumberSizePlayer == 0b010 || NumberSizePlayer == 0b110:
            case 0b111001 when NumberSizePlayer == 0b100 || NumberSizePlayer == 0b110:
                _pendingStart = GraphicsStartDelay;
                break;

            case 0b101101: // RESET
                Reset = true;
                _pendingStart = GraphicsStartDelay;
                break;
        }
    }

    /// <summary>
    /// Advances the player's one-colour-clock graphics pipeline and latches
    /// <see cref="PixelOn"/> for this colour clock. Real TIA clocks the
    /// serial graphics shift one colour clock ahead of the pixel it lights,
    /// so <see cref="_graphicsDelay"/> holds the bit sampled last call and
    /// that bit is what lights (or doesn't light) a pixel now.
    /// </summary>
    public void DoPlayer()
    {
        // Preroll between the copy-start decode and the graphics scan actually
        // beginning - see GraphicsStartDelay. A GRPx write that lands during
        // these clocks (Pitfall's score kernel writes one just ahead of each
        // copy) is therefore in place before the scan reads bit 7.
        if (_pendingStart > 0 && --_pendingStart == 0)
        {
            _draw = true;
            _scanCounter = 0b111;
            _scanCounterDivider = 0;
        }

        // The bit sampled on the previous colour clock is the one displayed
        // now - the deliberate one-clock delay (see the method summary).
        PixelOn = _graphicsDelay == 1;

        if (_graphicsDelay == 1)
        {
            _graphicsDelay = 0;
        }

        if (_draw)
        {
            // NUSIZ double/quad width advances the graphic scan only every
            // 2nd / 4th colour clock so each bit stretches to 2 / 4 pixels.
            // The bit is still re-sampled every colour clock, through the same
            // one-clock _graphicsDelay latch, so a held bit keeps lighting its
            // pixel between advances instead of gapping every other clock.
            _scanCounterDivider++;
            var advanceScan = _scanCounterDivider >= StretchFactor;
            if (advanceScan)
            {
                _scanCounterDivider = 0;
            }

            if (_scanCounter == 0b000 && advanceScan)
            {
                _draw = false;
            }

            // Handle reflection.
            var graphicsIndex = Reflect
                ? _scanCounter ^ 0b111
                : _scanCounter;

            _graphicsDelay = GetBit(ActiveGraphics, graphicsIndex);

            if (advanceScan)
            {
                if (_scanCounter == 0b000)
                {
                    _scanCounter = 0b111;
                }
                else
                {
                    _scanCounter--;
                }
            }
        }
    }

    /// <summary>
    /// Advances the missile's div-4 prescaler and LFSR counter, the same path
    /// <see cref="UpdatePlayerDiv4"/> runs for the player. While
    /// <see cref="MissileLockedToPlayer"/> (RESMP) the counter is instead held
    /// equal to the player counter, so releasing the lock leaves the missile
    /// aligned to the player copy start (see <see cref="MissileLockedToPlayer"/>).
    /// </summary>
    public void UpdateMissileDiv4()
    {
        if (MissileLockedToPlayer)
        {
            _missileCounter = _counter;
            MissileClockDiv4 = PlayerClockDiv4;
            return;
        }

        MissileClockDiv4++;

        if (MissileClockDiv4 > 3)
        {
            MissileClockDiv4 = 0;

            _missileCounter.Increment();
            if (_missileCounter.Value == 0b111111 || MissileReset)
            {
                MissileReset = false;
                _missileCounter.Reset();
            }

            ExecuteMissileLogic();
        }
    }

    /// <summary>
    /// Decides, at each missile counter step, whether a missile copy starts
    /// here. Reuses the player's NUSIZ D0-D2 copy decode so missile copies
    /// line up under the player copies; there is no 8-step scan, the missile
    /// is just switched on for <see cref="MissileWidth"/> colour clocks.
    /// </summary>
    private void ExecuteMissileLogic()
    {
        switch (_missileCounter.Value)
        {
            case 0b111000 when NumberSizePlayer == 0b001 || NumberSizePlayer == 0b011:
            case 0b101111 when NumberSizePlayer == 0b011 || NumberSizePlayer == 0b010 || NumberSizePlayer == 0b110:
            case 0b111001 when NumberSizePlayer == 0b100 || NumberSizePlayer == 0b110:
                _missilePendingStart = GraphicsStartDelay;
                break;

            case 0b101101: // Main copy - also self-resets the counter, exactly
                           // like the player, so copies repeat at a fixed period.
                MissileReset = true;
                _missilePendingStart = GraphicsStartDelay;
                break;
        }
    }

    /// <summary>
    /// Latches <see cref="MissilePixelOn"/> for this colour clock. Carries the
    /// same one-colour-clock render delay the player's serial graphics shift
    /// has (<see cref="_missileDrawNext"/> holds the bit decided last call),
    /// so a missile copy sits directly under its player copy.
    /// </summary>
    public void DoMissile()
    {
        MissilePixelOn = _missileDrawNext;
        _missileDrawNext = false;

        if (_missilePendingStart > 0 && --_missilePendingStart == 0)
        {
            _missilePixelsRemaining = MissileWidth;
        }

        if (_missilePixelsRemaining > 0)
        {
            _missilePixelsRemaining--;

            // RESMP forces the missile dark for as long as it is locked to
            // the player.
            _missileDrawNext = MissileEnabled && !MissileLockedToPlayer;
        }
    }
}
