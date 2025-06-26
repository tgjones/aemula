using System.Collections.Generic;
using Aemula.Chips.Tia.UI;
using static Aemula.Core.BitUtility;
using static Aemula.Chips.Tia.TiaUtility;
using Aemula.Core.UI;

namespace Aemula.Chips.Tia;

public sealed class Tia
{
    public TiaPins Pins;

    internal bool VerticalSync;
    internal bool VerticalBlank;

    internal bool HorizontalSync;
    internal bool HorizontalBlank;

    internal PolynomialCounter HorizontalCounter;
    private bool _horizontalReset;

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

    private byte _playfieldColor;
    private byte _playfieldLuminance;

    private byte _backgroundColor;
    private byte _backgroundLuminance;

    private bool _hmove;
    private bool _hmp0Latch;
    private bool _hmp1Latch;
    private byte _hmoveComparator;
    private bool _hmoveCounterEnabled;

    public Tia()
    {
        PlayerAndMissile0 = new PlayerAndMissile();
        PlayerAndMissile1 = new PlayerAndMissile();
    }

    public void Cycle()
    {
        // TODO

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
                Pins.Rdy = false;
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

                _hmoveComparator = (byte)((_hmoveComparator - 1) & 0b1111);
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
        }

        if (_playerCounterEnable)
        {
            PlayerAndMissile0.UpdatePlayerDiv4();
            PlayerAndMissile1.UpdatePlayerDiv4();
        }

        DoPlayfield();

        Pins.Blk = VerticalBlank || HorizontalBlank;
        Pins.Sync = VerticalSync || HorizontalSync;
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

            case 0b001111: // ColorBurst
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

    private void DoPlayfield()
    {
        // TODO: Reflect playfield

        var shouldOutputPlayfield = (_playfieldCanReflect && _playfieldReflect)
            ? GetBitAsBoolean(_playfield, _playfieldIndex)
            : GetBitAsBoolean(_playfield, 19 - _playfieldIndex);

        if (shouldOutputPlayfield)
        {
            if (_playfieldScore)
            {
                // Display the left side of the playfield using the color of sprite 0,
                // and the right side of the playfield using the color of sprite 1.
                if (_playfieldCanReflect)
                {
                    Pins.Lum = PlayerAndMissile1.Luminance;
                    Pins.Col = PlayerAndMissile1.Color;
                }
                else
                {
                    Pins.Lum = PlayerAndMissile0.Luminance;
                    Pins.Col = PlayerAndMissile0.Color;
                }
            }
            else
            {
                Pins.Lum = _playfieldLuminance;
                Pins.Col = _playfieldColor;
            }
        }
        else
        {
            Pins.Lum = _backgroundLuminance;
            Pins.Col = _backgroundColor;
        }

        PlayerAndMissile0.DoPlayer(this);
        PlayerAndMissile1.DoPlayer(this);

        if (HorizontalBlank || VerticalBlank)
        {
            Pins.Lum = 0;
            Pins.Col = 0;
        }
    }

    public void CpuCycle()
    {
        ref var pins = ref Pins;

        if (pins.RW)
        {
            // Read registers.
            //console.log(`TIA read register. Address = ${toHexString(pins.address, 2)}`);

            switch (pins.Address)
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

            switch (pins.Address)
            {
                // VSYNC - Vertical sync set/clear
                case 0x00:
                    VerticalSync = GetBitAsBoolean(pins.Data05, 1);
                    break;

                // VBLANK - Vertical blank set/clear
                case 0x01:
                    VerticalBlank = GetBitAsBoolean(pins.Data05, 1);
                    _i45Enable = GetBitAsBoolean(pins.Data67, 0);
                    _i03DumpToGround = GetBitAsBoolean(pins.Data67, 1);
                    break;

                // WSYNC - Wait for sync. Halts microprocessor by clearing RDY latch to zero.
                // RDY is set to false again by leading edge of horizontal blank.
                case 0x02:
                    pins.Rdy = true;
                    break;

                // RSYNC - Reset horizontal sync counter.
                case 0x03:
                    _horizontalReset = true;
                    break;

                // NUSIZ0 - Number-size player-missile 0
                case 0x04:
                    PlayerAndMissile0.NumberSizePlayer = (byte)(pins.Data05 & 0b111);
                    PlayerAndMissile0.NumberSizeMissile = (byte)(pins.Data05 >> 3);
                    break;

                // NUSIZ1 - Number-size player-missile 1
                case 0x05:
                    PlayerAndMissile1.NumberSizePlayer = (byte)(pins.Data05 & 0b111);
                    PlayerAndMissile1.NumberSizeMissile = (byte)(pins.Data05 >> 3);
                    break;

                // COLUP0 - Color-luminance player 0
                case 0x06:
                    PlayerAndMissile0.Color = (byte)((pins.Data05 >> 4) | (pins.Data67 << 2));
                    PlayerAndMissile0.Luminance = (byte)((pins.Data05 >> 1) & 0b111);
                    break;

                // COLUP1 - Color-luminance player 1
                case 0x07:
                    PlayerAndMissile1.Color = (byte)((pins.Data05 >> 4) | (pins.Data67 << 2));
                    PlayerAndMissile1.Luminance = (byte)((pins.Data05 >> 1) & 0b111);
                    break;

                // COLUPF - Color-luminance playfield
                case 0x08:
                    _playfieldColor = (byte)((pins.Data05 >> 4) | (pins.Data67 << 2));
                    _playfieldLuminance = (byte)((pins.Data05 >> 1) & 0b111);
                    break;

                // COLUBK - Color-luminance background
                case 0x09:
                    _backgroundColor = (byte)((pins.Data05 >> 4) | (pins.Data67 << 2));
                    _backgroundLuminance = (byte)((pins.Data05 >> 1) & 0b111);
                    break;

                // CTRLPF - Control playfield ball size and collisions
                case 0x0A:
                    // TODO
                    _playfieldReflect = GetBitAsBoolean(pins.Data05, 0);
                    _playfieldScore = GetBitAsBoolean(pins.Data05, 1);
                    break;

                // REFP0 - Reflect player 0
                case 0x0B:
                    PlayerAndMissile0.Reflect = GetBitAsBoolean(pins.Data05, 3);
                    break;

                // REFP1 - Reflect player 1
                case 0x0C:
                    PlayerAndMissile1.Reflect = GetBitAsBoolean(pins.Data05, 3);
                    break;

                // PF0 - Playfield register byte 0
                //   D4 => PF19
                //   D5 => PF18
                //   D6 => PF17
                //   D7 => PF16
                case 0x0D:
                    {
                        var temp =
                            (GetBit(pins.Data05, 4) << 3) |
                            (GetBit(pins.Data05, 5) << 2) |
                            (GetBit(pins.Data67, 0) << 1) |
                            (GetBit(pins.Data67, 1) << 0);
                        _playfield = (ushort)((temp << 16) | (_playfield & 0xFFFF));
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
                        var temp = (byte)(pins.Data05 | (pins.Data67 << 6));
                        _playfield = (ushort)((_playfield & 0xF00FF) | (temp << 8));
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
                            (GetBit(pins.Data05, 0) << 7) |
                            (GetBit(pins.Data05, 1) << 6) |
                            (GetBit(pins.Data05, 2) << 5) |
                            (GetBit(pins.Data05, 3) << 4) |
                            (GetBit(pins.Data05, 4) << 3) |
                            (GetBit(pins.Data05, 5) << 2) |
                            (GetBit(pins.Data67, 0) << 1) |
                            (GetBit(pins.Data67, 1) << 0);
                        _playfield = (ushort)((_playfield & 0xFFF00) | temp);
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
                    break;

                // RESM1 - Reset missile 1
                case 0x13:
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
                    PlayerAndMissile0.Graphics = (byte)(pins.Data05 | (pins.Data67 << 6));
                    break;

                // GRP1 - Graphics player 1
                case 0x1C:
                    PlayerAndMissile1.Graphics = (byte)(pins.Data05 | (pins.Data67 << 6));
                    break;

                // ENAM0 - Graphics (enable) missile 0
                case 0x1D:
                    break;

                // ENAM1 - Graphics (enable) missile 1
                case 0x1E:
                    break;

                // ENABL - Graphics (enable) ball
                case 0x1F:
                    break;

                // HMP0 - Horizontal motion player 0
                case 0x20:
                    // Invert HM bit 3 to simplify counting
                    PlayerAndMissile0.HorizontalMotionPlayer = (byte)
                        ((pins.Data05 >> 4) |
                        ((pins.Data67 & 1) << 2) |
                        ((pins.Data67 >> 1) == 1 ? 0b0000 : 0b1000));
                    break;

                // HMP1 - Horizontal motion player 1
                case 0x21:
                    PlayerAndMissile1.HorizontalMotionPlayer = (byte)
                        ((pins.Data05 >> 4) |
                        ((pins.Data67 & 1) << 2) |
                        ((pins.Data67 >> 1) == 1 ? 0b0000 : 0b1000));
                    break;

                // HMM0 - Horizontal motion missile 0
                case 0x22:
                    break;

                // HMM1 - Horizontal motion missile 1
                case 0x23:
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
                case 0x28:
                    break;

                // RESMP1 - Reset missile 1 to player 1
                case 0x29:
                    break;

                // HMOVE - Apply horizontal motion
                case 0x2A:
                    _hmove = true;
                    _hmp0Latch = true;
                    _hmp1Latch = true;
                    _hmoveComparator = 0b1111;
                    _hmoveCounterEnabled = true;
                    break;

                // HMCLR - Clear horizontal motion registers
                case 0x2B:
                    PlayerAndMissile0.HorizontalMotionPlayer = 0b1000;
                    PlayerAndMissile1.HorizontalMotionPlayer = 0b1000;
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

    public IEnumerable<DebuggerWindow> CreateDebuggerWindows()
    {
        yield return new TiaWindow(this);
    }
}
