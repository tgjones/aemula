namespace Aemula.Emulation.Chips.Ricoh2C02;

// Composite-video signal generation. The 2C02 builds the whole NTSC waveform on
// a single pin - sync, blanking, colour burst and an 8-level luma/phase DAC - so
// this file reproduces that behaviourally: each PPU dot picks one DAC tap pair
// (from the rendered background colour inside active picture, or the current
// sync / blank / burst region otherwise), and a free-running 12-state Johnson
// counter (the "chroma phase", clocked at 2x f_SC) chooses the _l or _h member
// of that pair per 12x-f_SC cell. NesSystem.CompositeVideo pulls those cells via
// NextVideoCell() - two per master clock - and box-averages groups of three down
// to the Television's 4x-f_SC sample rate.
//
// The dot / scanline numbers, the burst chroma-phase position and the hue
// direction below were calibrated node-for-node against the transistor-level
// FlawlessChips.Flawless2C02 oracle (see Aemula.Tests Ricoh2C02Tests). Two
// framing facts fell out of that calibration:
//
//  * The 2C02 emits picture (the last pixel / backdrop colour) for ~283 dots per
//    line, not 256 - only a ~58-dot window carries front porch / sync / breezeway
//    / burst / back porch. CurrentDot here counts the same as Flawless's hpos.
//
//  * Every video-region edge lands on a half-dot boundary (the pclk0 -> pclk1
//    point). UpdateVideoSignal runs once per dot from CycleDot and its region
//    applies to the eight 12x cells that follow, which line up with the second
//    half of this dot plus the first half of the next - so the constants below
//    are the hpos values at which each region *starts*, and a region [a..b]
//    means dots a..b inclusive drive that part of the waveform.
partial class Ricoh2C02Chip
{
    // --- DAC level table (arbitrary units) ------------------------------------
    //
    // One linear scale for every video tap, with the sync tip at 0. The luma
    // pairs are Bisqwit's NES palette-generator constants (his levels[8], given
    // relative to sync voltage) multiplied by 1000:
    //
    //   Bisqwit: black = 0.518, white = 1.962
    //            levels[8] = { 0.350, 0.518, 0.962, 1.550,   // signal low,  luma 0..3
    //                          1.094, 1.506, 1.962, 1.962 }   // signal high, luma 0..3
    //
    // Colour burst is not part of levels[8], so its two levels come from
    // lidnariq's terminated die measurements on the NESDev "NTSC video" page
    // (SYNC 48 mV, blanking 312 mV, CBL 148 mV, CBH 524 mV) carried onto this
    // scale by the sync->blanking fit  units = (mV - 48) * 518 / (312 - 48):
    // CBL -> 196, CBH -> 934, straddling the 518 blanking level.
    //
    // NESDev's measured luma levels match Bisqwit to ~1 unit at the dark end
    // (0D 228 mV -> 353, 1D 312 mV -> 518) but run ~3-5% higher at the bright
    // end (2D 552 mV -> 989 vs 962, 20 1100 mV -> 2064 vs 1962). The plan
    // standardises on Bisqwit's levels[8]; the absolute scale is irrelevant
    // anyway because NesSystem.CompositeVideo re-anchors on sync / blanking /
    // white before handing bytes to the Television.
    //
    // How the hue codes use these pairs (NESDev "NTSC video"): $x1..$xC emit a
    // square wave alternating _l (the $xD level) and _h (the $x0 level); $x0 is
    // a constant _h (grey, no chroma); $xD is a constant _l (dark grey);
    // $xE/$xF sit at the blanking level (same voltage as $1D).
    // internal so NesSystem.CompositeVideo can anchor its DAC-code -> byte map on
    // exactly these two points (sync tip and blanking).
    internal const ushort DacSyncLow = 0;    // vid_sync_l  - sync tip (Bisqwit 0.0)
    internal const ushort DacSyncHigh = 518; // vid_sync_h  - blanking (Bisqwit black)
    private const ushort DacBurstLow = 196;  // vid_burst_l - NESDev CBL 148 mV
    private const ushort DacBurstHigh = 934; // vid_burst_h - NESDev CBH 524 mV

    // vid_luma{0..3}_l - the $xD levels, indexed by the 2-bit luma code
    // (LLHH bits 4-5). Bisqwit levels[0..3] x 1000.
    private static readonly ushort[] DacLumaLow =
    {
        350,
        518,
        962,
        1550,
    };

    // vid_luma{0..3}_h - the $x0 levels. Bisqwit levels[4..7] x 1000; luma
    // codes 2 and 3 both clip at white (1962), matching the 2C02 DAC (NESDev
    // has no $30 measurement either).
    private static readonly ushort[] DacLumaHigh =
    {
        1094,
        1506,
        1962,
        1962,
    };

    // --- Horizontal timing (dots == Flawless2C02 hpos) ------------------------
    private const int FrontPorchFirstDot = 271;
    private const int FrontPorchLastDot = 279;
    private const int HorizontalSyncFirstDot = 280;
    private const int HorizontalSyncLastDot = 304;
    private const int BreezewayFirstDot = 305;
    private const int BreezewayLastDot = 308;
    private const int BurstFirstDot = 309;
    private const int BurstLastDot = 323;
    private const int BackPorchFirstDot = 324;
    private const int BackPorchLastDot = 328;

    // Picture is emitted for dots >= PictureResumeDot (the tail that feeds the
    // next line) and dots <= PictureHeadLastDot (the head of this line).
    private const int PictureResumeDot = 329;
    private const int PictureHeadLastDot = 270;

    // --- Vertical timing (scanlines == Flawless2C02 vpos) ---------------------
    //
    // Post-render (240) and pre-render (261) still carry an active-video-level
    // signal (the backdrop colour); scanlines 241-260 are the vertical blanking
    // interval. The 2C02 does not emit CCIR-style equalizing pulses: it just
    // drops to a broad serrated vertical-sync pulse from scanline 244 dot 280
    // through scanline 247 dot 256, with a blanking notch over dots 257-279 on
    // the serrated lines.
    private const int PostRenderScanline = 240;
    private const int FirstVBlankScanline = 241;
    private const int VerticalSyncBroadStartScanline = 244;
    private const int VerticalSyncSerratedFirstScanline = 245;
    private const int VerticalSyncSerratedLastScanline = 246;
    private const int VerticalSyncBroadEndScanline = 247;
    private const int PreRenderScanline = 261;
    private const int SerrationNotchFirstDot = 257;
    private const int SerrationNotchLastDot = 279;

    // Chroma phase (0..11) at which the burst square wave is emitted. The
    // Johnson counter runs at 2x f_SC; with the ((hue + phase) % 12) < 6 rule
    // (hue direction below), driving it from this virtual "hue" lands the burst
    // where Flawless2C02 emits it, which is NESDev's phase-8 position (the
    // orphaned prototype's phase-6 guess was one f_SC quarter-cycle off).
    private const int BurstHue = 8;

    private enum VideoTapColumn
    {
        Sync,
        Burst,
        Luma0,
        Luma1,
        Luma2,
        Luma3,
    }

    // The digital video tap nodes the 2C02 exposes: exactly one column is active
    // at a time, and within it the _h vs _l member follows the chroma square
    // wave. Mirrors Flawless2C02's vid_sync_{h,l} / vid_burst_{h,l} /
    // vid_luma{0..3}_{h,l} pins; consumed by the Flawless2C02 comparison test.
    internal readonly record struct VideoTaps(
        bool SyncH, bool SyncL,
        bool BurstH, bool BurstL,
        bool Luma0H, bool Luma0L,
        bool Luma1H, bool Luma1L,
        bool Luma2H, bool Luma2L,
        bool Luma3H, bool Luma3L);

    // The 12-state Johnson-counter chroma phase (0..11), clocked at 2x f_SC.
    // Free-running: never reset anywhere, so the odd-frame dot skip on the
    // pre-render line (see CycleDot()) leaves the dot<->subcarrier relationship
    // frame-coherent on its own, exactly as the shortened odd field does in
    // hardware.
    private int _chromaPhase;

    private VideoTapColumn _videoTapColumn;

    // When false, every 12x cell of this dot emits _videoConstantLevel and the
    // tap's _h member is chosen iff _videoConstantIsHigh. When true, the cell
    // alternates between _videoLevelLow / _videoLevelHigh by the
    // ((hue + phase) % 12) < 6 rule.
    private bool _videoAlternates;
    private bool _videoConstantIsHigh;
    private ushort _videoConstantLevel;
    private ushort _videoLevelLow;
    private ushort _videoLevelHigh;
    private int _videoHue;

    // The _h vs _l choice for the cell most recently produced by NextVideoCell().
    private bool _videoCellIsHigh;

    /// <summary>
    /// Advances the 12x-f_SC chroma phase by one cell and returns that cell's DAC
    /// code (arbitrary units - see the level table above). Called by
    /// <c>NesSystem.CompositeVideo</c> twice per master <c>Tick()</c> (8 times per
    /// PPU dot); it box-averages groups of three cells down to the Television's
    /// 4x-f_SC sample rate.
    /// </summary>
    internal ushort NextVideoCell()
    {
        _chromaPhase++;
        if (_chromaPhase == 12)
        {
            _chromaPhase = 0;
        }

        if (!_videoAlternates)
        {
            _videoCellIsHigh = _videoConstantIsHigh;
            return _videoConstantLevel;
        }

        // hue 1..12; the _h member of the tap pair is selected when
        // ((hue + phase) % 12) < 6 (hue direction calibrated against Flawless2C02
        // over hues $1/$6/$C).
        _videoCellIsHigh = ((_videoHue + _chromaPhase) % 12) < 6;
        return _videoCellIsHigh ? _videoLevelHigh : _videoLevelLow;
    }

    /// <summary>
    /// The digital video-tap node state for the cell most recently produced by
    /// <see cref="NextVideoCell"/>. Read once per 12x cell by the Flawless2C02
    /// comparison test.
    /// </summary>
    internal VideoTaps SampleVideoTaps()
    {
        var high = _videoCellIsHigh;
        var low = !high;

        return _videoTapColumn switch
        {
            VideoTapColumn.Sync => new VideoTaps(SyncH: high, SyncL: low,
                false, false, false, false, false, false, false, false, false, false),
            VideoTapColumn.Burst => new VideoTaps(false, false, BurstH: high, BurstL: low,
                false, false, false, false, false, false, false, false),
            VideoTapColumn.Luma0 => new VideoTaps(false, false, false, false,
                Luma0H: high, Luma0L: low, false, false, false, false, false, false),
            VideoTapColumn.Luma1 => new VideoTaps(false, false, false, false,
                false, false, Luma1H: high, Luma1L: low, false, false, false, false),
            VideoTapColumn.Luma2 => new VideoTaps(false, false, false, false,
                false, false, false, false, Luma2H: high, Luma2L: low, false, false),
            VideoTapColumn.Luma3 => new VideoTaps(false, false, false, false,
                false, false, false, false, false, false, Luma3H: high, Luma3L: low),
            _ => default,
        };
    }

    /// <summary>
    /// Test hook: places the video state machine at a known dot / scanline with a
    /// known chroma phase and dot-clock alignment, so the Flawless2C02 comparison
    /// can start from one of that oracle's preset states.
    /// </summary>
    internal void SeedVideoState(ulong scanline, ulong dot, int chromaPhase)
    {
        CurrentScanline = scanline;
        CurrentDot = dot;
        _clk = false;
        _clkDivideCounter = 7;
        _chromaPhase = ((chromaPhase % 12) + 12) % 12;
    }

    /// <summary>
    /// Test hook: writes palette RAM directly, bypassing the CPU VRAM handshake.
    /// </summary>
    internal void SetPaletteMemory(byte offset, byte value)
    {
        _paletteMemory[offset] = value;
    }

    /// <summary>
    /// Updates the composite-video region and DAC-tap selection for the current
    /// PPU dot. Called once per dot from <see cref="CycleDot"/> after the render
    /// pipeline has produced <see cref="CurrentBackgroundColor"/>.
    /// </summary>
    private void UpdateVideoSignal()
    {
        var scanline = (int)CurrentScanline;
        var dot = (int)CurrentDot;

        // Broad serrated vertical-sync pulse (no equalizing pulses on the 2C02).
        if (scanline == VerticalSyncBroadStartScanline && dot >= HorizontalSyncFirstDot)
        {
            SetSyncTip();
            return;
        }
        if (scanline >= VerticalSyncSerratedFirstScanline && scanline <= VerticalSyncSerratedLastScanline)
        {
            if (dot >= SerrationNotchFirstDot && dot <= SerrationNotchLastDot)
            {
                SetBlanking();
            }
            else
            {
                SetSyncTip();
            }
            return;
        }
        if (scanline == VerticalSyncBroadEndScanline && dot < SerrationNotchFirstDot)
        {
            SetSyncTip();
            return;
        }

        // Horizontal blanking window: front porch / sync / breezeway / burst /
        // back porch. Everything outside it is picture (or blanked picture).
        if (dot >= FrontPorchFirstDot && dot <= FrontPorchLastDot)
        {
            SetBlanking();          // front porch
            return;
        }
        if (dot >= HorizontalSyncFirstDot && dot <= HorizontalSyncLastDot)
        {
            SetSyncTip();           // horizontal sync
            return;
        }
        if (dot >= BreezewayFirstDot && dot <= BreezewayLastDot)
        {
            SetBlanking();          // breezeway
            return;
        }
        if (dot >= BurstFirstDot && dot <= BurstLastDot)
        {
            // Colour burst rides on blanking on every line that reaches here -
            // the only lines without it are the broad vertical-sync pulses
            // (244-246), which are handled above and never fall through.
            SetBurst();
            return;
        }
        if (dot >= BackPorchFirstDot && dot <= BackPorchLastDot)
        {
            SetBlanking();          // back porch
            return;
        }

        // Picture region. The head (dots <= 270) still carries picture through
        // the first vblank line (241); the tail (dots >= 329) carries it through
        // post-render (240) and again on the pre-render line, feeding the next
        // line's head.
        var head = dot <= PictureHeadLastDot;
        var emitPicture = head
            ? scanline <= FirstVBlankScanline
            : scanline <= PostRenderScanline || scanline == PreRenderScanline;

        if (emitPicture)
        {
            SetActivePicture(CurrentBackgroundColor);
        }
        else
        {
            SetBlanking();
        }
    }

    private void SetBlanking()
    {
        _videoTapColumn = VideoTapColumn.Sync;
        _videoAlternates = false;
        _videoConstantIsHigh = true;
        _videoConstantLevel = DacSyncHigh;
    }

    private void SetSyncTip()
    {
        _videoTapColumn = VideoTapColumn.Sync;
        _videoAlternates = false;
        _videoConstantIsHigh = false;
        _videoConstantLevel = DacSyncLow;
    }

    private void SetBurst()
    {
        _videoTapColumn = VideoTapColumn.Burst;
        _videoAlternates = true;
        _videoHue = BurstHue;
        _videoLevelLow = DacBurstLow;
        _videoLevelHigh = DacBurstHigh;
    }

    private void SetActivePicture(byte color)
    {
        // LLHH: bits 0-3 hue code, bits 4-5 luma code. Colour emphasis ($2001
        // bits 5-7) is deliberately unmodelled - this is where the ~120-degree-
        // wide chroma pull-down for an emphasised sub-band would be applied.
        var hue = color & 0x0F;
        var luma = (color >> 4) & 0x03;

        _videoTapColumn = luma switch
        {
            0 => VideoTapColumn.Luma0,
            1 => VideoTapColumn.Luma1,
            2 => VideoTapColumn.Luma2,
            _ => VideoTapColumn.Luma3,
        };

        switch (hue)
        {
            case 0x00:
                // Constant _h - grey, no chroma.
                _videoAlternates = false;
                _videoConstantIsHigh = true;
                _videoConstantLevel = DacLumaHigh[luma];
                break;

            case 0x0D:
                // Constant _l - dark grey.
                _videoAlternates = false;
                _videoConstantIsHigh = false;
                _videoConstantLevel = DacLumaLow[luma];
                break;

            case 0x0E:
            case 0x0F:
                // Blanking level - black (same tap as ordinary picture-area
                // blanking: vid_sync_h at the blanking voltage).
                _videoTapColumn = VideoTapColumn.Sync;
                _videoAlternates = false;
                _videoConstantIsHigh = true;
                _videoConstantLevel = DacSyncHigh;
                break;

            default:
                // Hue 1..12 - chroma square wave against the phase counter.
                _videoAlternates = true;
                _videoHue = hue;
                _videoLevelLow = DacLumaLow[luma];
                _videoLevelHigh = DacLumaHigh[luma];
                break;
        }
    }
}
