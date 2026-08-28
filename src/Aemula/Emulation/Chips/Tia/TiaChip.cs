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
    private byte _i;

    /// <summary>
    /// Dumped and latched inputs.
    /// Dumped inputs (I0..I3) are used for paddles.
    /// Latched inputs (I4..I5) are used for joystick / paddle triggers.
    /// The setter samples the I4/I5 trigger latches on every change so a
    /// momentary low pulse is caught even if the pin is high again before the
    /// program reads INPT4/INPT5 (see <see cref="UpdateTriggerLatches"/>).
    /// </summary>
    public byte I
    {
        get => _i;
        set
        {
            _i = value;
            UpdateTriggerLatches();
        }
    }

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

                    // The ball rides the same comparator as the players and
                    // missiles - one latch, cleared when the count matches
                    // HMBL.
                    if (NoneEqual(_hmoveComparator, Ball.HorizontalMotion))
                    {
                        _hmblLatch = false;
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

                if (_hmblLatch)
                {
                    Ball.UpdateDiv4();
                }
            }

            if (_playerCounterEnable)
            {
                PlayerAndMissile0.UpdatePlayerDiv4();
                PlayerAndMissile1.UpdatePlayerDiv4();
                PlayerAndMissile0.UpdateMissileDiv4();
                PlayerAndMissile1.UpdateMissileDiv4();
                Ball.UpdateDiv4();
            }

            // DoVideo() is deliberately NOT called here. The pixel for this
            // colour clock is rendered by RenderColorClock(), which the system
            // calls *after* the 6507's bus write for this same tick has been
            // applied (TiaChip.Phi2). Rendering here instead would sample the
            // graphics/colour/playfield latches one colour clock before a
            // same-cycle TIA store lands - the exact off-by-one that clipped
            // the left edge of a digit in Pitfall's streaming score kernel,
            // whose GRPx rewrites are timed to within ~1 colour clock of the
            // player copy they feed.
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

                // Collision reads. Address is already masked to 6 bits by the
                // system, and the canonical CX registers sit at 0x30-0x37;
                // each returns one pair latch on D7 and (all but CXBLPF) a
                // second on D6, driven out through Data67 (bit 0 -> D6,
                // bit 1 -> D7). The system merges Data67 << 6 back onto the
                // CPU bus and supplies D0-D5 from the bus itself.
                //
                // The eight CX registers plus the six 0x38-0x3D input ports
                // are driven here. Everything else is true open bus, so
                // Data67 - a persistent property - is deliberately left
                // untouched for those rather than being forced to a value.
                //
                // The input ports drive D7 only; D6 is undefined on them and
                // reads back 0, the same way CXBLPF's unused D6 does.
                switch (Address)
                {
                    // CXM0P
                    case 0x30:
                        Data67 = PackData67(
                            d7: Collisions.IsSet(CollisionLatches.M0P1),
                            d6: Collisions.IsSet(CollisionLatches.M0P0));
                        break;

                    // CXM1P
                    case 0x31:
                        Data67 = PackData67(
                            d7: Collisions.IsSet(CollisionLatches.M1P0),
                            d6: Collisions.IsSet(CollisionLatches.M1P1));
                        break;

                    // CXP0FB
                    case 0x32:
                        Data67 = PackData67(
                            d7: Collisions.IsSet(CollisionLatches.P0PF),
                            d6: Collisions.IsSet(CollisionLatches.P0BL));
                        break;

                    // CXP1FB
                    case 0x33:
                        Data67 = PackData67(
                            d7: Collisions.IsSet(CollisionLatches.P1PF),
                            d6: Collisions.IsSet(CollisionLatches.P1BL));
                        break;

                    // CXM0FB
                    case 0x34:
                        Data67 = PackData67(
                            d7: Collisions.IsSet(CollisionLatches.M0PF),
                            d6: Collisions.IsSet(CollisionLatches.M0BL));
                        break;

                    // CXM1FB
                    case 0x35:
                        Data67 = PackData67(
                            d7: Collisions.IsSet(CollisionLatches.M1PF),
                            d6: Collisions.IsSet(CollisionLatches.M1BL));
                        break;

                    // CXBLPF - D6 is unused and always reads 0.
                    case 0x36:
                        Data67 = PackData67(
                            d7: Collisions.IsSet(CollisionLatches.BLPF),
                            d6: false);
                        break;

                    // CXPPMM
                    case 0x37:
                        Data67 = PackData67(
                            d7: Collisions.IsSet(CollisionLatches.P0P1),
                            d6: Collisions.IsSet(CollisionLatches.M0M1));
                        break;

                    // INPT0-INPT3 - dumped paddle inputs. There is no analog
                    // paddle RC model, so a set I bit stands in for "cap
                    // charged" (D7 = 1). VBLANK D7 dumps the caps to ground,
                    // forcing D7 = 0 regardless of the pin.
                    case 0x38:
                        Data67 = PackData67(d7: !_i03DumpToGround && GetBitAsBoolean(_i, 0), d6: false);
                        break;

                    case 0x39:
                        Data67 = PackData67(d7: !_i03DumpToGround && GetBitAsBoolean(_i, 1), d6: false);
                        break;

                    case 0x3A:
                        Data67 = PackData67(d7: !_i03DumpToGround && GetBitAsBoolean(_i, 2), d6: false);
                        break;

                    case 0x3B:
                        Data67 = PackData67(d7: !_i03DumpToGround && GetBitAsBoolean(_i, 3), d6: false);
                        break;

                    // INPT4 / INPT5 - latched joystick trigger inputs. With
                    // latching disabled the pin passes straight through; with
                    // it enabled an SR latch (updated on every pin change and
                    // on the VBLANK write) holds D7 low once the pin has been
                    // seen low.
                    case 0x3C:
                        Data67 = PackData67(d7: GetBitAsBoolean(_i, 4) && !_inpt4LatchedLow, d6: false);
                        break;

                    case 0x3D:
                        Data67 = PackData67(d7: GetBitAsBoolean(_i, 5) && !_inpt5LatchedLow, d6: false);
                        break;

                    // Not a register this stage drives - leave Data67 as-is.
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
                        if (!_i45Enable)
                        {
                            // Latching disabled holds both trigger latches
                            // reset, so the I4/I5 pin level passes straight
                            // through on the next INPT4/INPT5 read.
                            _inpt4LatchedLow = false;
                            _inpt5LatchedLow = false;
                        }
                        else
                        {
                            // Enabling latching mid-frame with a pin already
                            // held low latches immediately, matching the real
                            // SR latch (and Stella's LatchedInput).
                            UpdateTriggerLatches();
                        }
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
                    case 0x0A:
                        _playfieldReflect = GetBitAsBoolean(Data05, 0);
                        _playfieldScore = GetBitAsBoolean(Data05, 1);
                        // D2 (PFP): move the playfield/ball priority group
                        // above both players. The resolver needs this, and
                        // it also disables score mode while set.
                        _playfieldPriority = GetBitAsBoolean(Data05, 2);
                        // D4-D5: ball width, 1 / 2 / 4 / 8 colour clocks.
                        Ball.Width = (byte)(1 << ((Data05 >> 4) & 0b11));
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
                            // PF0 lands in display-order bits 19..16; PF1/PF2
                            // in bits 15..0 are untouched.
                            _playfield = (uint)temp << 16 | _playfield & 0xFFFF;
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
                            // PF1 lands in display-order bits 15..8; the mask
                            // keeps PF0 (19..16) and PF2 (7..0).
                            _playfield = _playfield & 0xF00FF | (uint)temp << 8;
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
                            // PF2 lands in display-order bits 7..0; the mask
                            // keeps PF0 (19..16) and PF1 (15..8).
                            _playfield = _playfield & 0xFFF00 | (uint)temp;
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
                        Ball.Reset = true;
                        Ball.ClockDiv4 = 0;
                        break;

                    // AUDC0 - Audio control 0 (D0-D3 waveform / poly select)
                    case 0x15:
                        _audio0.Audc = (byte)(Data05 & 0b1111);
                        break;

                    // AUDC1 - Audio control 1
                    case 0x16:
                        _audio1.Audc = (byte)(Data05 & 0b1111);
                        break;

                    // AUDF0 - Audio frequency 0 (D0-D4 divisor)
                    case 0x17:
                        _audio0.Audf = (byte)(Data05 & 0b11111);
                        break;

                    // AUDF1 - Audio frequency 1
                    case 0x18:
                        _audio1.Audf = (byte)(Data05 & 0b11111);
                        break;

                    // AUDV0 - Audio volume 0 (D0-D3)
                    case 0x19:
                        _audio0.Audv = (byte)(Data05 & 0b1111);
                        break;

                    // AUDV1 - Audio volume 1
                    case 0x1A:
                        _audio1.Audv = (byte)(Data05 & 0b1111);
                        break;

                    // GRP0 - Graphics player 0. Writes P0's "new" graphics
                    // latch, and clocks P1's "old" latch from P1's "new" - the
                    // cross-player copy that makes VDELP1 lag by one write.
                    case 0x1B:
                        PlayerAndMissile0.WriteGraphics((byte)(Data05 | Data67 << 6));
                        PlayerAndMissile1.LatchDelayedGraphics();
                        break;

                    // GRP1 - Graphics player 1. Writes P1's "new" graphics
                    // latch, and clocks the "old" latches of both P0 and the
                    // ball - the strobe every two-line kernel leans on to
                    // advance its delayed objects.
                    case 0x1C:
                        PlayerAndMissile1.WriteGraphics((byte)(Data05 | Data67 << 6));
                        PlayerAndMissile0.LatchDelayedGraphics();
                        Ball.LatchDelayedEnable();
                        break;

                    // ENAM0 - Graphics (enable) missile 0
                    case 0x1D:
                        PlayerAndMissile0.MissileEnabled = GetBitAsBoolean(Data05, 1);
                        break;

                    // ENAM1 - Graphics (enable) missile 1
                    case 0x1E:
                        PlayerAndMissile1.MissileEnabled = GetBitAsBoolean(Data05, 1);
                        break;

                    // ENABL - Graphics (enable) ball. D1 writes the ball's
                    // "new" enable latch. The copy across to "old" is done by
                    // the GRP1 strobe, not here; VDELBL then selects which
                    // latch the drawing path reads.
                    case 0x1F:
                        Ball.WriteEnable(GetBitAsBoolean(Data05, 1));
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

                    // HMBL - Horizontal motion ball. Same signed encoding as
                    // HMP0/1 and HMM0/1; invert HM bit 3 to simplify the
                    // HMOVE comparator counting.
                    case 0x24:
                        Ball.HorizontalMotion = (byte)
                            (Data05 >> 4 |
                            (Data67 & 1) << 2 |
                            (Data67 >> 1 == 1 ? 0b0000 : 0b1000));
                        break;

                    // VDELP0 - Vertical delay player 0. D0 latches the mux that
                    // makes the drawing path read P0's "old" graphics latch.
                    case 0x25:
                        PlayerAndMissile0.VerticalDelay = GetBitAsBoolean(Data05, 0);
                        break;

                    // VDELP1 - Vertical delay player 1.
                    case 0x26:
                        PlayerAndMissile1.VerticalDelay = GetBitAsBoolean(Data05, 0);
                        break;

                    // VDELBL - Vertical delay ball. D0 latches the mux that
                    // makes the drawing path read the ball's "old" enable latch.
                    case 0x27:
                        Ball.VerticalDelay = GetBitAsBoolean(Data05, 0);
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

                    // HMOVE - Apply horizontal motion. _hmove drives both the
                    // comparator that gives each object its extra clocks and
                    // the extended-HBLANK comb (see ExecuteClockLogic). Only
                    // the normal "strobe just after WSYNC, inside HBLANK" case
                    // is modelled: strobing HMOVE late in the visible line
                    // (near colour clock 74) produces partial motion and a
                    // ragged comb on real hardware, which this does not
                    // reproduce.
                    case 0x2A:
                        _hmove = true;
                        _hmp0Latch = true;
                        _hmp1Latch = true;
                        _hmm0Latch = true;
                        _hmm1Latch = true;
                        _hmblLatch = true;
                        _hmoveComparator = 0b1111;
                        _hmoveCounterEnabled = true;
                        break;

                    // HMCLR - Clear horizontal motion registers
                    case 0x2B:
                        PlayerAndMissile0.HorizontalMotionPlayer = 0b1000;
                        PlayerAndMissile1.HorizontalMotionPlayer = 0b1000;
                        PlayerAndMissile0.HorizontalMotionMissile = 0b1000;
                        PlayerAndMissile1.HorizontalMotionMissile = 0b1000;
                        Ball.HorizontalMotion = 0b1000;
                        break;

                    // CXCLR - Clear collision latches
                    case 0x2C:
                        Collisions.Clear();
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

    // INPT4/INPT5 SR-latch outputs. Set once the matching I pin has been seen
    // low while _i45Enable is set; held until _i45Enable is cleared, which
    // resets both. Only meaningful while _i45Enable is set - kept false
    // otherwise so a read can treat "not latched" as "pass the pin through".
    private bool _inpt4LatchedLow;
    private bool _inpt5LatchedLow;

    /// <summary>
    /// Captures a low level on I4/I5 into the trigger latches while latching
    /// is enabled. A no-op when <see cref="_i45Enable"/> is clear (the latches
    /// are held reset then). Called from the <see cref="I"/> setter and after
    /// a VBLANK write so a brief press is latched regardless of when - or
    /// whether - the program reads the port.
    /// </summary>
    private void UpdateTriggerLatches()
    {
        if (!_i45Enable)
        {
            return;
        }

        if (!GetBitAsBoolean(_i, 4))
        {
            _inpt4LatchedLow = true;
        }

        if (!GetBitAsBoolean(_i, 5))
        {
            _inpt5LatchedLow = true;
        }
    }

    /// <summary>
    /// The 20-bit playfield graphic, PF0/PF1/PF2 merged into one word in
    /// display order: bit 19 is the first pixel drawn in each half, bit 0 the
    /// last. PF0 occupies bits 19..16, PF1 bits 15..8, PF2 bits 7..0, with
    /// each register's own bit order already normalised by the write cases.
    /// Held in a <see cref="uint"/> rather than a <see cref="ushort"/> so
    /// PF0's four bits survive - a narrower store silently dropped them, which
    /// left a 16-pixel dead band at the start of every playfield half.
    /// </summary>
    private uint _playfield;

    internal byte ClockDiv4;

    internal readonly PlayerAndMissile PlayerAndMissile0;
    internal readonly PlayerAndMissile PlayerAndMissile1;
    internal readonly Ball Ball;

    /// <summary>
    /// The two audio channels (AUDC0/AUDF0/AUDV0 -> AUD0, and channel 1 ->
    /// AUD1). Ticked twice per scanline from <see cref="ExecuteClockLogic"/>,
    /// which then drives the <see cref="Aud0"/>/<see cref="Aud1"/> pins from
    /// each channel's 1-bit waveform output.
    /// </summary>
    internal TiaAudioChannel _audio0;
    internal TiaAudioChannel _audio1;

    private bool _playerCounterEnable;

    /// <summary>
    /// The six raw per-object presence bits for the colour clock most
    /// recently rendered by <see cref="DoVideo"/>, captured before the
    /// priority resolver runs. Collision detection reads these (a collision
    /// is registered for any overlapping pair, whatever the resolver drew);
    /// nothing in the video path consumes them.
    /// </summary>
    internal ObjectPixels CurrentObjectPixels;

    /// <summary>
    /// The 15 sticky object-pair collision latches (CXM0P..CXPPMM). Fed one
    /// colour clock at a time from <see cref="CurrentObjectPixels"/> while the
    /// beam is in active display, read back through the 0x30-0x37 register
    /// decode, and cleared by a CXCLR (0x2C) write.
    /// </summary>
    internal CollisionLatches Collisions;

    private byte _playfieldIndex;

    /// <summary>
    /// Set true at the "Center" horizontal-counter state and cleared at the
    /// start of every line, so it reads as "the beam has passed the centre of
    /// the visible line". "Center" is where the playfield restarts for its
    /// mirrored right half (<see cref="_playfieldIndex"/> is reset to 0 there
    /// too) - i.e. exactly the playfield's left/right half boundary, PF pixel
    /// 80 of the 160 visible. Score mode flips its playfield tint from COLUP0
    /// to COLUP1 at that same boundary, so this one position flag feeds both
    /// (passed to <see cref="ResolveVideoOutput"/> as its left/right
    /// selector). It is purely a horizontal-position signal: CTRLPF D0
    /// (reflect) is the separate <see cref="_playfieldReflect"/> field and
    /// does not move this split.
    /// </summary>
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
    private bool _hmblLatch;
    private byte _hmoveComparator;
    private bool _hmoveCounterEnabled;

    public TiaChip()
    {
        PlayerAndMissile0 = new PlayerAndMissile();
        PlayerAndMissile1 = new PlayerAndMissile();
        Ball = new Ball();
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

            // Normal end of horizontal blank. Skipped when HMOVE was strobed
            // this line: the blank then runs on to the "Late Reset HBLANK"
            // state two counter states (8 colour clocks) further on, which is
            // the HMOVE comb - real hardware holds the beam blanked over the
            // leftmost 8 visible pixels on every line an HMOVE fires, so they
            // come out border-black.
            case 0b011100: // Reset HBLANK
                _playfieldIndex = 0;
                if (!_hmove)
                {
                    HorizontalBlank = false;
                    _playerCounterEnable = true;
                }
                break;

            // The delayed end of horizontal blank on an HMOVE line - the
            // other half of the comb above. The playfield counter is not
            // stalled by HMOVE, so it has already advanced two cells while the
            // comb hid them; starting it at cell 2 here keeps the playfield
            // aligned to its absolute screen position rather than shifting it
            // right by 8 pixels.
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
                // Second of the two audio clocks per line. The real TIA clocks
                // its audio twice a scanline - once near HBLANK start, once
                // near centre (Andrew Towers' TIA_HW_Notes) - which is
                // 2 x 15.7 kHz ~= 31.4 kHz on NTSC.
                _audio0.Tick();
                _audio1.Tick();
                break;

            // End of the 160-pixel visible region. Re-assert horizontal blank
            // here rather than waiting for the counter wrap one state later:
            // that one extra state was leaving 4 colour clocks of background
            // showing past pixel 160, making the active line measure 164.
            case 0b010100: // RESET
                _playerCounterEnable = false;
                _playfieldIndex = 0x14;
                _horizontalReset = true;
                _hmove = false;
                HorizontalBlank = true;
                // First of the two audio clocks per line, as the visible
                // region ends and horizontal blank re-asserts; the second is
                // at the "Center" state above. Two ticks per scanline is the
                // real TIA's audio rate (~31.4 kHz on NTSC).
                _audio0.Tick();
                _audio1.Tick();
                break;

            default:
                _playfieldIndex++;
                break;
        }

        Aud0 = _audio0.Output;
        Aud1 = _audio1.Output;
    }

    /// <summary>
    /// Renders the pixel for the colour clock that just advanced in the
    /// <see cref="Osc"/> step. Split out from the Osc step so the system can
    /// apply the 6507's same-tick TIA bus write (<see cref="Phi2"/>) first -
    /// see the comment where <see cref="DoVideo"/> used to be called.
    /// </summary>
    internal void RenderColorClock()
    {
        DoVideo();
    }

    private void DoVideo()
    {
        // PF cell 0..19 across a half maps to _playfield bit 19..0 in the
        // normal case (bit 19 first). The right half is drawn mirrored only
        // when CTRLPF D0 (reflect) is set, and _playfieldCanReflect is exactly
        // "beam is in the right half" (set at the Center state, cleared at
        // line start), so reflected-right reads bit 0..19 instead.
        var playfieldBit = _playfieldCanReflect && _playfieldReflect
            ? GetBitAsBoolean(_playfield, _playfieldIndex)
            : GetBitAsBoolean(_playfield, 19 - _playfieldIndex);

        // Step 1: every object reports "is my pixel lit here?" for this
        // colour clock. Step 2 (ResolveVideoOutput) picks a single winner.
        //
        // A missile shares its player's colour and priority slot, so it is
        // OR'd into that player's bit here. The ball shares the playfield's
        // priority slot but keeps its own COLUPF colour, so it is passed
        // separately.
        PlayerAndMissile0.DoPlayer();
        PlayerAndMissile1.DoPlayer();
        PlayerAndMissile0.DoMissile();
        PlayerAndMissile1.DoMissile();
        Ball.DoBall();

        // Publish the unresolved presence bits before the resolver runs, so
        // collision detection can see every overlap rather than just the
        // winning pixel.
        CurrentObjectPixels.Player0 = PlayerAndMissile0.PixelOn;
        CurrentObjectPixels.Missile0 = PlayerAndMissile0.MissilePixelOn;
        CurrentObjectPixels.Player1 = PlayerAndMissile1.PixelOn;
        CurrentObjectPixels.Missile1 = PlayerAndMissile1.MissilePixelOn;
        CurrentObjectPixels.Playfield = playfieldBit;
        CurrentObjectPixels.Ball = Ball.PixelOn;

        // Latch every object-pair overlap for this colour clock. Gated to the
        // active display: the object serial graphics run through blanking, but
        // TIA's BLANK signal suppresses their picture output there and the
        // collision latches follow the same visible-region gating. (Stella
        // gates collision updates on vertical blank only; also excluding
        // horizontal blank costs nothing visible here, since anything an
        // object draws inside HBLANK is off-screen regardless.)
        if (!(HorizontalBlank || VerticalBlank))
        {
            Collisions.Accumulate(CurrentObjectPixels);
        }

        ResolveVideoOutput(
            // A missile shares its player's colour and priority slot, so it
            // is OR'd into that player's bit for the resolver.
            player0: CurrentObjectPixels.Player0 || CurrentObjectPixels.Missile0,
            player1: CurrentObjectPixels.Player1 || CurrentObjectPixels.Missile1,
            playfield: CurrentObjectPixels.Playfield,
            ball: CurrentObjectPixels.Ball,
            // _playfieldCanReflect is set at the "Center" horizontal-counter
            // state and cleared at line start, so it also reads as "past
            // screen centre" - the left/right selector score mode needs. See
            // that field's remarks for why the split lands on the playfield
            // half boundary.
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
    /// Priority order, per the Stella Programmer's Guide and Stella's own
    /// TIA renderPixel:
    ///   normal            P0/M0 -> P1/M1 -> PF -> BL -> BK
    ///   CTRLPF D2 (PFP)   PF -> BL -> P0/M0 -> P1/M1 -> BK
    ///   score (CTRLPF D1) P0/M0 -> PF -> P1/M1 -> BL -> BK
    /// Within the PF/BL group the playfield is always tested first, so on a
    /// playfield/ball overlap the playfield's colour wins - the two only
    /// differ in score mode, where PF takes a player's colour and BL keeps
    /// COLUPF. Score mode also lifts the playfield above player 1: borrowing
    /// a player's colour, it borrows that player's priority slot too (COLUP0
    /// left of centre, COLUP1 right of it). PFP suppresses score mode
    /// entirely. <paramref name="pastScreenCentre"/> is the left/right
    /// selector for the score-mode tint.
    /// </summary>
    internal void ResolveVideoOutput(
        bool player0, bool player1, bool playfield, bool ball, bool pastScreenCentre)
    {
        if (_playfieldPriority)
        {
            // PFP: the playfield/ball group is composited above the players,
            // and score mode is disabled - so both draw in COLUPF and their
            // intra-group order (PF ahead of BL) makes no visible difference.
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
        else if (_playfieldScore && playfield)
        {
            // Score mode: the playfield bit takes a player's colour - COLUP0
            // left of screen centre, COLUP1 right of it - and with that
            // colour it takes that player's priority slot, so it outranks
            // player 1 / missile 1 here (only player 0 / missile 0, tested
            // above, still cover it). The ball is unaffected: it stays COLUPF
            // and stays in the low PF/BL group below.
            var player = pastScreenCentre ? PlayerAndMissile1 : PlayerAndMissile0;
            Lum = player.Luminance;
            Col = player.Color;
        }
        else if (player1)
        {
            Lum = PlayerAndMissile1.Luminance;
            Col = PlayerAndMissile1.Color;
        }
        else if (playfield)
        {
            // Outside score mode the playfield uses its own COLUPF and sits
            // just above the ball, below both players.
            Lum = _playfieldLuminance;
            Col = _playfieldColor;
        }
        else if (ball)
        {
            // The ball is tested after the playfield, so a coincident PF bit
            // wins the colour; the ball keeps COLUPF in every mode, score
            // mode included.
            Lum = _playfieldLuminance;
            Col = _playfieldColor;
        }
        else
        {
            Lum = _backgroundLuminance;
            Col = _backgroundColor;
        }
    }

    /// <summary>
    /// Packs a collision register's two latch bits into a <see cref="Data67"/>
    /// value: <paramref name="d6"/> drives data pin D6 (bit 0),
    /// <paramref name="d7"/> drives D7 (bit 1), matching the CX register
    /// layout.
    /// </summary>
    private static byte PackData67(bool d7, bool d6) =>
        (byte)((d7 ? 0b10 : 0) | (d6 ? 0b01 : 0));

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
