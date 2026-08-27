using System.Collections.Generic;
using Aemula.Emulation.Chips.Tia.UI;
using static Aemula.BitUtility;
using static Aemula.Emulation.Chips.Tia.TiaUtility;
using Aemula.UI;
using Aemula.UI.LogicAnalyzer;

namespace Aemula.Emulation.Chips.Tia;

public sealed class TiaChip
{
    // Pins

    /// <summary>
    /// Ready pin (output).
    /// </summary>
    public bool Rdy { get; private set; }

    /// <summary>
    /// Combined vertical and horizontal sync pin (output).
    /// </summary>
    public bool Sync { get; private set; }

    /// <summary>
    /// Address pins A0..A5 (input).
    /// </summary>
    public byte Address { get; set; }

    /// <summary>
    /// Processor data pins. Pins 0 to 5 are inputs only.
    /// </summary>
    public byte Data05 { get; set; }

    /// <summary>
    /// Processor data pins. Pins 6 and 7 are bidirectional.
    /// </summary>
    public byte Data67 { get; set; }

    /// <summary>
    /// Read/write pin (input). Read = true, write = false.
    /// </summary>
    public bool RW { get; set; }

    /// <summary>
    /// Video luminance output (3 pins, LUM0..LUM2). Written exactly once per
    /// colour clock by the priority resolver (<see cref="ResolveVideoOutput"/>),
    /// then forced to 0 during blanking.
    /// </summary>
    public byte Lum { get; private set; }

    // TODO: This should be a single pin. From the spec:
    // "A digital phase shifter is included on this chip to provide a
    // single color output with fifteen (15) phase angles."
    // But for now we just output a 4-bit colour.
    //
    // Deliberately not fixed by this comment's own TODO, after actually
    // designing and measuring the real-pin-accurate version: real Col is
    // one analog pin carrying a phase-shifted square wave at the subcarrier
    // rate (confirmed square, not sine - see an AtariAge thread specifically
    // on this question), which would mean a *stored*, oversampled pin - e.g.
    // 16 positions per tick,
    // matching the real 15 phase taps - updated every Osc edge alongside
    // Sync/Blk/Lum, rather than this plain hue index.
    //
    // That's a strictly more hardware-faithful shape, but it doesn't
    // actually make the composite-video output more *accurate*, because of
    // what has to happen to it downstream: Television.Decode only accepts
    // 4 samples per subcarrier cycle, so an oversampled pin's real square
    // wave still has to get reduced to 4 before it can be used. Measured
    // both ways (box-averaging 16 sub-positions down to 4, vs. directly
    // synthesizing a sine at the hue's phase - see
    // Atari2600System.CompositeVideo.cs): the averaged square wave decodes
    // with real, non-trivial error (up to ~24 degrees of hue rotation and
    // ~29% saturation swing between hues that should be equally saturated -
    // comparable to landing a whole hue-step off), while the synthesized
    // sine decodes exactly, with zero error - not a smoother approximation
    // but the mathematically exact answer, since a pure sinusoid sampled at
    // exactly its own period has no aliasing to lose information to. That
    // makes sense physically too: every real path from TIA to a receiver -
    // the stock RF modulator, or a composite mod's own resistor/cap network
    // - filters the raw square wave before anything downstream samples it,
    // and a sine is a much closer stand-in for that filtered signal than
    // the raw square wave's own harmonics would be.
    //
    // So this stays a plain hue index rather than becoming an oversampled
    // pin, and Atari2600System.CompositeVideo.cs reads it directly to
    // synthesize the composite chroma sample itself, rather than sampling a
    // TIA-level waveform - a deliberate accuracy-over-literalism trade, not
    // an oversight. The real oversampled pin is still worth building
    // eventually (e.g. so a future TIA logic-analyzer view can show Col's
    // actual waveform rather than this simplified index), just not before
    // something other than composite video actually needs it.
    /// <summary>
    /// Video color output. Written exactly once per colour clock by the
    /// priority resolver (<see cref="ResolveVideoOutput"/>), then overridden
    /// to the color-burst / black reference during blanking.
    /// </summary>
    public byte Col { get; private set; }

    /// <summary>
    /// Combined vertical and horizontal blank output.
    /// </summary>
    public bool Blk { get; private set; }

    /// <summary>
    /// Color delay input.
    /// </summary>
    public bool Del { get; set; }

    /// <summary>
    /// Audio output 0.
    /// </summary>
    public bool Aud0 { get; private set; }

    /// <summary>
    /// Audio output 1.
    /// </summary>
    public bool Aud1 { get; private set; }

    // TODO: May need to split these into separate pins.
    /// <summary>
    /// Dumped and latched inputs.
    /// Dumped inputs (I0..I3) are used for paddles.
    /// Latched inputs (I4..I5) are used for joystick / paddle triggers.
    /// </summary>
    public byte I { get; set; }

    private bool _cs0;
    /// <summary>
    /// Chip select 0 (input). Wired to A12 on real hardware - active low.
    /// </summary>
    public bool CS0 { get => _cs0; set => _cs0 = value; }

    private bool _cs1;
    /// <summary>
    /// Chip select 1 (input). Tied to +5V on real hardware - active high.
    /// </summary>
    public bool CS1 { get => _cs1; set => _cs1 = value; }

    private bool _cs2;
    /// <summary>
    /// Chip select 2 (input). Tied to GND on real hardware - active low.
    /// </summary>
    public bool CS2 { get => _cs2; set => _cs2 = value; }

    private bool _cs3;
    /// <summary>
    /// Chip select 3 (input). Wired to A7 on real hardware - active low.
    /// </summary>
    public bool CS3 { get => _cs3; set => _cs3 = value; }

    /// <summary>
    /// Whether the chip-select pins currently indicate this TIA is selected.
    /// With CS0/CS3 wired to A12/A7 (active low) and CS1/CS2 tied to fixed
    /// +5V/GND levels (active high/low respectively), this reduces to "A7 and
    /// A12 are both low", matching the documented net effect.
    /// </summary>
    private bool Selected => !_cs0 && _cs1 && !_cs2 && !_cs3;

    private bool _osc;

    /// <summary>
    /// Master oscillator clock input. Real TIA is clocked at 3.579545MHz
    /// (the NTSC color subcarrier rate) via this pin.
    /// </summary>
    public bool Osc
    {
        get => _osc;
        set
        {
            if (_osc == value)
            {
                return;
            }

            _osc = value;

            if (!value)
            {
                return;
            }

            // TIA divides OSC by 3 internally to generate the 6507's clock
            // (Phi0), matching the real 3.579545MHz -> 1.193182MHz relationship.
            // _phi0Divider reflects the count as of the start of this edge, the
            // same point at which the old Atari2600System.Tick()'s _tiaCycle
            // check ran before calling _tia.Cycle().
            _phi0 = _phi0Divider == 0;

            ClockDiv4++;
            if (ClockDiv4 > 3)
            {
                ClockDiv4 = 0;

                HorizontalCounter.Increment();

                if (HorizontalCounter.Value == 0b111111 || _horizontalReset)
                {
                    _playfieldCanReflect = false;
                    _playfieldIndex = 0;
                    _horizontalReset = false;
                    HorizontalCounter.Reset();
                    HorizontalBlank = true;
                    Rdy = false;
                }

                ExecuteClockLogic();

                if (_hmoveCounterEnabled)
                {
                    if (NoneEqual(_hmoveComparator, PlayerAndMissile0.HorizontalMotionPlayer))
                    {
                        _hmp0Latch = false;
                    }

                    if (NoneEqual(_hmoveComparator, PlayerAndMissile1.HorizontalMotionPlayer))
                    {
                        _hmp1Latch = false;
                    }

                    // Missiles ride the same comparator as the players - one
                    // latch each, cleared when the count matches that
                    // object's HM register.
                    if (NoneEqual(_hmoveComparator, PlayerAndMissile0.HorizontalMotionMissile))
                    {
                        _hmm0Latch = false;
                    }

                    if (NoneEqual(_hmoveComparator, PlayerAndMissile1.HorizontalMotionMissile))
                    {
                        _hmm1Latch = false;
                    }

                    _hmoveComparator = (byte)(_hmoveComparator - 1 & 0b1111);
                    if (_hmoveComparator == 0b1111)
                    {
                        _hmoveCounterEnabled = false;
                    }
                }

                if (_hmp0Latch)
                {
                    PlayerAndMissile0.UpdatePlayerDiv4();
                }

                if (_hmp1Latch)
                {
                    PlayerAndMissile1.UpdatePlayerDiv4();
                }

                if (_hmm0Latch)
                {
                    PlayerAndMissile0.UpdateMissileDiv4();
                }

                if (_hmm1Latch)
                {
                    PlayerAndMissile1.UpdateMissileDiv4();
                }
            }

            if (_playerCounterEnable)
            {
                PlayerAndMissile0.UpdatePlayerDiv4();
                PlayerAndMissile1.UpdatePlayerDiv4();
                PlayerAndMissile0.UpdateMissileDiv4();
                PlayerAndMissile1.UpdateMissileDiv4();
            }

            DoVideo();

            Blk = VerticalBlank || HorizontalBlank;
            Sync = VerticalSync || HorizontalSync;

            _phi0Divider++;
            if (_phi0Divider > 2)
            {
                _phi0Divider = 0;
            }
        }
    }

    private byte _phi0Divider;
    private bool _phi0;

    /// <summary>
    /// Clock output (to the 6507). Derived internally from <see cref="Osc"/>.
    /// </summary>
    public bool Phi0 => _phi0;

    private bool _phi2;

    /// <summary>
    /// Clock input (from the 6507's Phi2 output). Runs a register read/write
    /// on the rising edge, gated on the chip being <see cref="Selected"/>.
    /// </summary>
    public bool Phi2
    {
        get => _phi2;
        set
        {
            if (_phi2 == value)
            {
                return;
            }

            _phi2 = value;

            if (!value || !Selected)
            {
                return;
            }

            if (RW)
            {
                // Read registers.
                //console.log(`TIA read register. Address = ${toHexString(pins.address, 2)}`);

                switch (Address)
                {
                    // CXM0P - Read collision
                    case 0x00:
                        break;

                    // TODO

                    // Ignore invalid addresses
                    default:
                        break;
                }
            }
            else
            {
                // Write registers.
                //console.log(`TIA write register. Address = ${toHexString(pins.address, 2)}. Data67 = ${toHexString(pins.data67, 2)}, Data05 = ${toHexString(pins.data05, 2)}`);

                switch (Address)
                {
                    // VSYNC - Vertical sync set/clear
                    case 0x00:
                        VerticalSync = GetBitAsBoolean(Data05, 1);
                        break;

                    // VBLANK - Vertical blank set/clear
                    case 0x01:
                        VerticalBlank = GetBitAsBoolean(Data05, 1);
                        _i45Enable = GetBitAsBoolean(Data67, 0);
                        _i03DumpToGround = GetBitAsBoolean(Data67, 1);
                        break;

                    // WSYNC - Wait for sync. Halts microprocessor by clearing RDY latch to zero.
                    // RDY is set to false again by leading edge of horizontal blank.
                    case 0x02:
                        Rdy = true;
                        break;

                    // RSYNC - Reset horizontal sync counter.
                    case 0x03:
                        _horizontalReset = true;
                        break;

                    // NUSIZ0 - Number-size player-missile 0
                    case 0x04:
                        PlayerAndMissile0.NumberSizePlayer = (byte)(Data05 & 0b111);
                        PlayerAndMissile0.NumberSizeMissile = (byte)(Data05 >> 3);
                        break;

                    // NUSIZ1 - Number-size player-missile 1
                    case 0x05:
                        PlayerAndMissile1.NumberSizePlayer = (byte)(Data05 & 0b111);
                        PlayerAndMissile1.NumberSizeMissile = (byte)(Data05 >> 3);
                        break;

                    // COLUP0 - Color-luminance player 0
                    case 0x06:
                        PlayerAndMissile0.Color = (byte)(Data05 >> 4 | Data67 << 2);
                        PlayerAndMissile0.Luminance = (byte)(Data05 >> 1 & 0b111);
                        break;

                    // COLUP1 - Color-luminance player 1
                    case 0x07:
                        PlayerAndMissile1.Color = (byte)(Data05 >> 4 | Data67 << 2);
                        PlayerAndMissile1.Luminance = (byte)(Data05 >> 1 & 0b111);
                        break;

                    // COLUPF - Color-luminance playfield
                    case 0x08:
                        _playfieldColor = (byte)(Data05 >> 4 | Data67 << 2);
                        _playfieldLuminance = (byte)(Data05 >> 1 & 0b111);
                        break;

                    // COLUBK - Color-luminance background
                    case 0x09:
                        _backgroundColor = (byte)(Data05 >> 4 | Data67 << 2);
                        _backgroundLuminance = (byte)(Data05 >> 1 & 0b111);
                        break;

                    // CTRLPF - Control playfield ball size and collisions
                    // TODO: D4-D5 select the ball width (1/2/4/8 clocks).
                    case 0x0A:
                        _playfieldReflect = GetBitAsBoolean(Data05, 0);
                        _playfieldScore = GetBitAsBoolean(Data05, 1);
                        // D2 (PFP): move the playfield/ball priority group
                        // above both players. The resolver needs this, and
                        // it also disables score mode while set.
                        _playfieldPriority = GetBitAsBoolean(Data05, 2);
                        break;

                    // REFP0 - Reflect player 0
                    case 0x0B:
                        PlayerAndMissile0.Reflect = GetBitAsBoolean(Data05, 3);
                        break;

                    // REFP1 - Reflect player 1
                    case 0x0C:
                        PlayerAndMissile1.Reflect = GetBitAsBoolean(Data05, 3);
                        break;

                    // PF0 - Playfield register byte 0
                    //   D4 => PF19
                    //   D5 => PF18
                    //   D6 => PF17
                    //   D7 => PF16
                    case 0x0D:
                        {
                            var temp =
                                GetBit(Data05, 4) << 3 |
                                GetBit(Data05, 5) << 2 |
                                GetBit(Data67, 0) << 1 |
                                GetBit(Data67, 1) << 0;
                            _playfield = (ushort)(temp << 16 | _playfield & 0xFFFF);
                            break;
                        }

                    // PF1 - Playfield register byte 1
                    //   D0 => PF08
                    //   D1 => PF09
                    //   D2 => PF10
                    //   D3 => PF11
                    //   D4 => PF12
                    //   D5 => PF13
                    //   D6 => PF14
                    //   D7 => PF15
                    case 0x0E:
                        {
                            var temp = (byte)(Data05 | Data67 << 6);
                            _playfield = (ushort)(_playfield & 0xF00FF | temp << 8);
                            break;
                        }

                    // PF2 - Playfield register byte 2
                    //   D0 => P7
                    //   D1 => P6
                    //   D2 => P5
                    //   D3 => P4
                    //   D4 => P3
                    //   D5 => P2
                    //   D6 => P1
                    //   D7 => P0
                    case 0x0F:
                        {
                            var temp =
                                GetBit(Data05, 0) << 7 |
                                GetBit(Data05, 1) << 6 |
                                GetBit(Data05, 2) << 5 |
                                GetBit(Data05, 3) << 4 |
                                GetBit(Data05, 4) << 3 |
                                GetBit(Data05, 5) << 2 |
                                GetBit(Data67, 0) << 1 |
                                GetBit(Data67, 1) << 0;
                            _playfield = (ushort)(_playfield & 0xFFF00 | temp);
                            break;
                        }

                    // RESP0 - Reset player 0
                    case 0x10:
                        PlayerAndMissile0.Reset = true;
                        PlayerAndMissile0.PlayerClockDiv4 = 0;
                        break;

                    // RESP1 - Reset player 1
                    case 0x11:
                        PlayerAndMissile1.Reset = true;
                        PlayerAndMissile1.PlayerClockDiv4 = 0;
                        break;

                    // RESM0 - Reset missile 0
                    case 0x12:
                        PlayerAndMissile0.MissileReset = true;
                        PlayerAndMissile0.MissileClockDiv4 = 0;
                        break;

                    // RESM1 - Reset missile 1
                    case 0x13:
                        PlayerAndMissile1.MissileReset = true;
                        PlayerAndMissile1.MissileClockDiv4 = 0;
                        break;

                    // RESBL - Reset ball
                    case 0x14:
                        break;

                    // AUDC0 - Audio control 0
                    case 0x15:
                        break;

                    // AUDC1 - Audio control 1
                    case 0x16:
                        break;

                    // AUDF0 - Audio frequency 0
                    case 0x17:
                        break;

                    // AUDF1 - Audio frequency 1
                    case 0x18:
                        break;

                    // AUDV0 - Audio volume 0
                    case 0x19:
                        break;

                    // AUDv1 - Audio volume 1
                    case 0x1A:
                        break;

                    // GRP0 - Graphics player 0
                    case 0x1B:
                        PlayerAndMissile0.Graphics = (byte)(Data05 | Data67 << 6);
                        break;

                    // GRP1 - Graphics player 1
                    case 0x1C:
                        PlayerAndMissile1.Graphics = (byte)(Data05 | Data67 << 6);
                        break;

                    // ENAM0 - Graphics (enable) missile 0
                    case 0x1D:
                        PlayerAndMissile0.MissileEnabled = GetBitAsBoolean(Data05, 1);
                        break;

                    // ENAM1 - Graphics (enable) missile 1
                    case 0x1E:
                        PlayerAndMissile1.MissileEnabled = GetBitAsBoolean(Data05, 1);
                        break;

                    // ENABL - Graphics (enable) ball
                    case 0x1F:
                        break;

                    // HMP0 - Horizontal motion player 0
                    case 0x20:
                        // Invert HM bit 3 to simplify counting
                        PlayerAndMissile0.HorizontalMotionPlayer = (byte)
                            (Data05 >> 4 |
                            (Data67 & 1) << 2 |
                            (Data67 >> 1 == 1 ? 0b0000 : 0b1000));
                        break;

                    // HMP1 - Horizontal motion player 1
                    case 0x21:
                        PlayerAndMissile1.HorizontalMotionPlayer = (byte)
                            (Data05 >> 4 |
                            (Data67 & 1) << 2 |
                            (Data67 >> 1 == 1 ? 0b0000 : 0b1000));
                        break;

                    // HMM0 - Horizontal motion missile 0
                    // Same signed encoding as HMP0/1; invert HM bit 3 to
                    // simplify the HMOVE comparator counting.
                    case 0x22:
                        PlayerAndMissile0.HorizontalMotionMissile = (byte)
                            (Data05 >> 4 |
                            (Data67 & 1) << 2 |
                            (Data67 >> 1 == 1 ? 0b0000 : 0b1000));
                        break;

                    // HMM1 - Horizontal motion missile 1
                    case 0x23:
                        PlayerAndMissile1.HorizontalMotionMissile = (byte)
                            (Data05 >> 4 |
                            (Data67 & 1) << 2 |
                            (Data67 >> 1 == 1 ? 0b0000 : 0b1000));
                        break;

                    // HMBL - Horizontal motion ball
                    case 0x24:
                        break;

                    // VDELP0 - Vertical delay player 0
                    case 0x25:
                        break;

                    // VDELP1 - Vertical delay player 1
                    case 0x26:
                        break;

                    // VDELBL - Vertical delay ball
                    case 0x27:
                        break;

                    // RESMP0 - Reset missile 0 to player 0
                    // D1 locks the missile onto the player: the missile
                    // counter tracks the player counter and the missile pixel
                    // is suppressed until the lock is cleared.
                    case 0x28:
                        PlayerAndMissile0.MissileLockedToPlayer = GetBitAsBoolean(Data05, 1);
                        break;

                    // RESMP1 - Reset missile 1 to player 1
                    case 0x29:
                        PlayerAndMissile1.MissileLockedToPlayer = GetBitAsBoolean(Data05, 1);
                        break;

                    // HMOVE - Apply horizontal motion
                    case 0x2A:
                        _hmove = true;
                        _hmp0Latch = true;
                        _hmp1Latch = true;
                        _hmm0Latch = true;
                        _hmm1Latch = true;
                        _hmoveComparator = 0b1111;
                        _hmoveCounterEnabled = true;
                        break;

                    // HMCLR - Clear horizontal motion registers
                    case 0x2B:
                        PlayerAndMissile0.HorizontalMotionPlayer = 0b1000;
                        PlayerAndMissile1.HorizontalMotionPlayer = 0b1000;
                        PlayerAndMissile0.HorizontalMotionMissile = 0b1000;
                        PlayerAndMissile1.HorizontalMotionMissile = 0b1000;
                        break;

                    // CXCLR - Clear collision latches
                    case 0x2C:
                        break;

                    // Ignore invalid addresses
                    default:
                        break;
                }
            }
        }
    }

    // Internal state

    internal bool VerticalSync;
    internal bool VerticalBlank;

    internal bool HorizontalSync;
    internal bool HorizontalBlank;

    internal PolynomialCounter HorizontalCounter;
    private bool _horizontalReset;

    /// <summary>
    /// Whether TIA is currently forcing its color output onto the same
    /// reference phase hue code 1 ("gold") uses, rather than whatever the
    /// game's most recently written COLUBK/COLUPF/COLUPx selected - the real
    /// color burst mechanism, per Andrew Towers' TIA Hardware Notes ("The
    /// TIA produces a reference color output (color burst) during
    /// horizontal blank..."): there's no separate burst oscillator or pin,
    /// just the same phase-shift tap COL always uses, temporarily forced to
    /// the reference position. Set one horizontal-counter state after
    /// Towers' "Reset HSYNC" control line (approximating real NTSC's
    /// breezeway gap - see <see cref="ExecuteClockLogic"/>) and cleared by
    /// his "RCB"/"Reset Colour Burst" line, so this rides on TIA's free-
    /// running horizontal timing exactly like every other blanking-interval
    /// signal - including on vertical-blanking lines, matching real
    /// broadcast NTSC (a receiver needs burst on every line to stay color-
    /// locked, not just during active picture lines). The breezeway gap
    /// isn't just cosmetic: without it, burst's own first sample would
    /// immediately follow HSYNC's trailing edge - exactly the sample
    /// NtscSyncSeparator uses to (re)calibrate its black-level estimate
    /// each line - so a burst *peak* landing there, instead of genuine flat
    /// blanking, drags that estimate away from the real black level, which
    /// in turn misclassifies burst's own low half as more sync (confirmed
    /// via a smoke test before this gap was added: the whole raster came
    /// back misclassified as one long HSYNC region).
    /// </summary>
    private bool _colorBurst;

    /// <summary>
    /// Controls whether latches I4..I5 are enabled.
    /// </summary>
    private bool _i45Enable;

    /// <summary>
    /// Controls whether latches I0..I3 are dumped to ground.
    /// </summary>
    private bool _i03DumpToGround;

    /// <summary>
    /// Stores combined values of PF0, PF1, PF2 registers.
    /// </summary>
    private ushort _playfield;

    internal byte ClockDiv4;

    internal readonly PlayerAndMissile PlayerAndMissile0;
    internal readonly PlayerAndMissile PlayerAndMissile1;

    private bool _playerCounterEnable;

    private byte _playfieldIndex;

    private bool _playfieldCanReflect;

    private bool _playfieldReflect;
    private bool _playfieldScore;

    /// <summary>
    /// CTRLPF D2 (PFP). When set, the playfield and ball are composited
    /// above both players instead of below them, and score mode is
    /// suppressed. Parsed from CTRLPF alongside reflect/score; consumed only
    /// by <see cref="ResolveVideoOutput"/>.
    /// </summary>
    private bool _playfieldPriority;

    private byte _playfieldColor;
    private byte _playfieldLuminance;

    private byte _backgroundColor;
    private byte _backgroundLuminance;

    private bool _hmove;
    private bool _hmp0Latch;
    private bool _hmp1Latch;
    private bool _hmm0Latch;
    private bool _hmm1Latch;
    private byte _hmoveComparator;
    private bool _hmoveCounterEnabled;

    public TiaChip()
    {
        PlayerAndMissile0 = new PlayerAndMissile();
        PlayerAndMissile1 = new PlayerAndMissile();
    }

    private void ExecuteClockLogic()
    {
        switch (HorizontalCounter.Value)
        {
            case 0b111100: // Set HSYNC
                HorizontalSync = true;
                break;

            case 0b110111: // Reset HSYNC
                HorizontalSync = false;
                break;

            // Not itself a control line named in Towers' notes - just the
            // next horizontal-counter state after "Reset HSYNC" above, used
            // here as the earliest available point (given this counter's
            // existing 4-color-clock state granularity) to start color
            // burst one state late rather than immediately on HSYNC's own
            // trailing edge. That gap approximates real NTSC's ~0.6us
            // "breezeway" between HSYNC and burst (coarser here - one 4-
            // color-clock state, versus spec's ~2.15 color clocks - since
            // Towers doesn't document a separately-named line for it) - see
            // _colorBurst's own remarks for why this gap matters, not just
            // for realism.
            case 0b111011:
                _colorBurst = true;
                break;

            case 0b001111: // Towers' "RCB" - Reset Colour Burst.
                _colorBurst = false;
                break;

            case 0b011100: // Reset HBLANK
                _playfieldIndex = 0;
                if (!_hmove)
                {
                    HorizontalBlank = false;
                    _playerCounterEnable = true;
                }
                break;

            case 0b010111: // Late Reset HBLANK, if HMOVE activated
                _playfieldIndex = 2;
                if (_hmove)
                {
                    HorizontalBlank = false;
                    _playerCounterEnable = true;
                }
                break;

            case 0b101100: // Center
                _playfieldCanReflect = true;
                _playfieldIndex = 0;
                break;

            case 0b010100: // RESET
                _playerCounterEnable = false;
                _playfieldIndex = 0x14;
                _horizontalReset = true;
                _hmove = false;
                // TODO: Tick audio
                break;

            default:
                _playfieldIndex++;
                break;
        }
    }

    private void DoVideo()
    {
        // TODO: Reflect playfield

        var playfieldBit = _playfieldCanReflect && _playfieldReflect
            ? GetBitAsBoolean(_playfield, _playfieldIndex)
            : GetBitAsBoolean(_playfield, 19 - _playfieldIndex);

        // Step 1: every object reports "is my pixel lit here?" for this
        // colour clock. Step 2 (ResolveVideoOutput) picks a single winner.
        //
        // A missile shares its player's colour and priority slot, so it is
        // OR'd into that player's bit here. The ball isn't modelled yet; it
        // enters as permanently-off so the resolver's priority ladder is
        // already wired for it. It shares the playfield's priority slot but
        // keeps its own COLUPF colour, so it is passed separately.
        PlayerAndMissile0.DoPlayer();
        PlayerAndMissile1.DoPlayer();
        PlayerAndMissile0.DoMissile();
        PlayerAndMissile1.DoMissile();

        const bool ballBit = false;

        ResolveVideoOutput(
            player0: PlayerAndMissile0.PixelOn || PlayerAndMissile0.MissilePixelOn,
            player1: PlayerAndMissile1.PixelOn || PlayerAndMissile1.MissilePixelOn,
            playfield: playfieldBit,
            ball: ballBit,
            // _playfieldCanReflect is set at the "Center" horizontal-counter
            // state and cleared at the start of each line, so it doubles as
            // "are we past screen centre?" - exactly the left/right selector
            // score mode needs.
            pastScreenCentre: _playfieldCanReflect);

        if (HorizontalBlank || VerticalBlank)
        {
            Lum = 0;

            // Hue code 1 is TIA's own reference phase ("gold... the same
            // phase as color burst" - Stella Programmer's Guide), so
            // forcing Col to 1 here, rather than 0, is what actually
            // transmits color burst - see _colorBurst's remarks.
            Col = _colorBurst ? (byte)1 : (byte)0;
        }
    }

    /// <summary>
    /// Picks the single winning object for the current colour clock and
    /// writes <see cref="Lum"/>/<see cref="Col"/> once from its colour
    /// registers. Replaces the former "playfield, then player 0, then
    /// player 1 each overwrite the pixel in turn" scheme, which gave
    /// player 1 priority over player 0 (backwards - player 0 must win) and
    /// left nowhere to slot missiles, the ball, the priority bit or the
    /// collision taps.
    ///
    /// <paramref name="player0"/>/<paramref name="player1"/> already fold in
    /// the matching missile (a missile shares its player's colour and
    /// priority slot). <paramref name="ball"/> stays separate from
    /// <paramref name="playfield"/> because the two share a priority slot but
    /// not a colour: the ball is always COLUPF, whereas a score-mode
    /// playfield takes a player's colour.
    ///
    /// Priority order, per the Stella Programmer's Guide:
    ///   normal            P0 -> P1 -> PF/BL -> BK
    ///   CTRLPF D2 (PFP)   PF/BL -> P0 -> P1 -> BK
    /// Score mode (CTRLPF D1) is suppressed while PFP is set. Otherwise a set
    /// playfield bit is drawn in COLUP0 on the left half of the screen and
    /// COLUP1 on the right - <paramref name="pastScreenCentre"/> is that
    /// left/right selector.
    /// </summary>
    internal void ResolveVideoOutput(
        bool player0, bool player1, bool playfield, bool ball, bool pastScreenCentre)
    {
        if (_playfieldPriority)
        {
            // PFP: the playfield/ball group is composited above the players,
            // and score mode is disabled - so the playfield always uses its
            // own colour here.
            if (playfield || ball)
            {
                Lum = _playfieldLuminance;
                Col = _playfieldColor;
            }
            else if (player0)
            {
                Lum = PlayerAndMissile0.Luminance;
                Col = PlayerAndMissile0.Color;
            }
            else if (player1)
            {
                Lum = PlayerAndMissile1.Luminance;
                Col = PlayerAndMissile1.Color;
            }
            else
            {
                Lum = _backgroundLuminance;
                Col = _backgroundColor;
            }

            return;
        }

        if (player0)
        {
            Lum = PlayerAndMissile0.Luminance;
            Col = PlayerAndMissile0.Color;
        }
        else if (player1)
        {
            Lum = PlayerAndMissile1.Luminance;
            Col = PlayerAndMissile1.Color;
        }
        else if (playfield)
        {
            if (_playfieldScore)
            {
                // Score mode: tint the playfield bit with a player's colour -
                // COLUP0 left of screen centre, COLUP1 right of it.
                var player = pastScreenCentre ? PlayerAndMissile1 : PlayerAndMissile0;
                Lum = player.Luminance;
                Col = player.Color;
            }
            else
            {
                Lum = _playfieldLuminance;
                Col = _playfieldColor;
            }
        }
        else if (ball)
        {
            // The ball keeps COLUPF even in score mode.
            Lum = _playfieldLuminance;
            Col = _playfieldColor;
        }
        else
        {
            Lum = _backgroundLuminance;
            Col = _backgroundColor;
        }
    }

    public void CreateDebuggerWindows(List<DebuggerWindow> result)
    {
        result.Add(new TiaWindow(this));
    }

    /// <summary>
    /// Owned here (rather than by the system that embeds a TIA) so every
    /// system gets the same channel list for free.
    /// </summary>
    internal ChannelGroup CreateChannelGroup()
    {
        return new ChannelGroup("TIA",
        [
            Channel.Bus("Address", 6, () => Address),
            Channel.Bus("Data0-5", 6, () => Data05),
            Channel.Bus("Data6-7", 2, () => Data67),
            Channel.Digital("R/W", () => RW),
            Channel.Digital("RDY", () => Rdy),
            Channel.Digital("SYNC", () => Sync),
            Channel.Digital("BLK", () => Blk),
            Channel.Bus("LUM", 3, () => Lum),
            Channel.Bus("COL", 4, () => Col),
            Channel.Digital("DEL", () => Del),
            Channel.Digital("AUD0", () => Aud0),
            Channel.Digital("AUD1", () => Aud1),
            Channel.Bus("I", 6, () => I),
            Channel.Digital("CS0", () => CS0),
            Channel.Digital("CS1", () => CS1),
            Channel.Digital("CS2", () => CS2),
            Channel.Digital("CS3", () => CS3),
            Channel.Digital("OSC", () => Osc),
            Channel.Digital("PHI0", () => Phi0),
            Channel.Digital("PHI2", () => Phi2),
        ]);
    }
}
