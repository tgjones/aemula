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
// Signal-level structure ported from the orphaned Ppu/Ricoh2C02 prototype; the
// exact dot / scanline numbers are calibrated against the transistor-level
// Flawless2C02 oracle in a later step.
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
    private const ushort DacSyncLow = 0;     // vid_sync_l  - sync tip (Bisqwit 0.0)
    private const ushort DacSyncHigh = 518;  // vid_sync_h  - blanking (Bisqwit black)
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

    // --- Horizontal timing (dots) ------------------------------------------------
    //
    // Anchored at the end of the 256-dot active picture window. Segment widths
    // are the prototype's (front porch 9, sync 25, breezeway 4, burst 15); the
    // prototype's HPos origin was unclear, so absolute dot numbers here are a
    // seed for the step-6 Flawless2C02 calibration.
    private const int HBlankFrontPorchStartDot = 257;
    private const int HBlankSyncTipStartDot = 266;
    private const int HBlankBreezewayStartDot = 291;
    private const int HBlankBurstStartDot = 295;   // colour burst ~10 f_SC cycles wide
    private const int HBlankBackPorchStartDot = 310;

    // --- Vertical timing (scanlines) -------------------------------------------
    //
    // Post-render (240) and pre-render (261) lines carry active-video-level
    // signal (the backdrop colour); scanlines 241-260 are the vertical blanking
    // interval. Within it, 241-247 replace the normal horizontal structure with
    // equalizing pulses (241-242, 246-247) and broad serrated vertical-sync
    // pulses (243-245) - the prototype's VPos 244-246 cases, shifted to this
    // chip's scanline origin. Step 6 calibrates the exact lines against
    // Flawless2C02.
    private const int PostRenderScanline = 240;
    private const int VerticalSyncFirstScanline = 241;
    private const int VerticalSyncLastScanline = 247;
    private const int BroadSyncFirstScanline = 243;
    private const int BroadSyncLastScanline = 245;
    private const int PreRenderScanline = 261;

    private const int HalfLineDots = 341 / 2;
    private const int EqualizingPulseWidthDots = 5;
    private const int SerrationNotchWidthDots = 24;

    // Burst is emitted at a fixed chroma-phase position. The prototype used hue
    // code 6 (EmitColor(0x06, ...)); its own comment flags that NESDev's "NTSC
    // video" page implies phase 8 instead. Kept as 6 here - which 12-phase cell
    // burst sits in is the single calibration landmark step 6 / step 8 pin down
    // so decoded hues land on _systemPalette.
    private const int BurstHue = 6;

    private enum VideoRegion
    {
        ActivePicture,
        Blanking,
        SyncTip,
        Burst,
    }

    // The 12-state Johnson-counter chroma phase (0..11), clocked at 2x f_SC.
    // Free-running: never reset anywhere, so the odd-frame dot skip on the
    // pre-render line (see Cycle()) leaves the dot<->subcarrier relationship
    // frame-coherent on its own, exactly as the shortened odd field does in
    // hardware.
    private int _chromaPhase;

    private VideoRegion _videoRegion;

    // When false, every 12x cell of this dot emits _videoConstantLevel. When
    // true, the cell alternates between _videoLevelLow / _videoLevelHigh by the
    // ((hue + phase) % 12) < 6 rule.
    private bool _videoAlternates;
    private ushort _videoConstantLevel;
    private ushort _videoLevelLow;
    private ushort _videoLevelHigh;
    private int _videoHue;

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
            return _videoConstantLevel;
        }

        // hue 1..12; the _h member of the tap pair is selected when
        // ((hue + phase) % 12) < 6. Hue direction (and therefore burst phase) is
        // a step-6 calibration landmark.
        return ((_videoHue + _chromaPhase) % 12) < 6 ? _videoLevelHigh : _videoLevelLow;
    }

    /// <summary>
    /// Updates the composite-video region and DAC-tap selection for the current
    /// PPU dot. Called once per dot from <see cref="Cycle"/> after the render
    /// pipeline has produced <see cref="CurrentBackgroundColor"/>.
    /// </summary>
    private void UpdateVideoSignal()
    {
        var scanline = (int)CurrentScanline;
        var dot = (int)CurrentDot;

        // Vertical-sync interval: equalizing + broad serrated pulses replace the
        // normal horizontal structure on these lines.
        if (scanline >= VerticalSyncFirstScanline && scanline <= VerticalSyncLastScanline)
        {
            var half = dot % HalfLineDots;
            var broad = scanline >= BroadSyncFirstScanline && scanline <= BroadSyncLastScanline;
            var syncTip = broad
                ? half >= SerrationNotchWidthDots   // broad pulse: sync low except a blanking notch per half-line
                : half < EqualizingPulseWidthDots;  // equalizing pulse: brief sync tip per half-line
            SetSimpleRegion(syncTip ? VideoRegion.SyncTip : VideoRegion.Blanking);
            return;
        }

        // Horizontal active-picture window (dots 1-256). Visible lines carry the
        // rendered pixel; post-render / pre-render lines carry the $3F00 backdrop
        // that RenderTick() leaves in CurrentBackgroundColor. Scanlines 241-260
        // are vertical blanking - picture area held at blanking level.
        if (dot >= 1 && dot <= 256)
        {
            var activePictureLine = scanline <= PostRenderScanline || scanline == PreRenderScanline;
            if (activePictureLine)
            {
                SetActivePictureRegion(CurrentBackgroundColor);
            }
            else
            {
                SetSimpleRegion(VideoRegion.Blanking);
            }
            return;
        }

        // Horizontal blanking on dots 257-340 plus the idle dot 0:
        // front porch / horizontal sync / breezeway / colour burst / back porch.
        if (dot == 0 || dot >= HBlankBackPorchStartDot)
        {
            SetSimpleRegion(VideoRegion.Blanking);          // back porch (and idle dot 0)
        }
        else if (dot < HBlankSyncTipStartDot)
        {
            SetSimpleRegion(VideoRegion.Blanking);          // front porch
        }
        else if (dot < HBlankBreezewayStartDot)
        {
            SetSimpleRegion(VideoRegion.SyncTip);           // horizontal sync
        }
        else if (dot < HBlankBurstStartDot)
        {
            SetSimpleRegion(VideoRegion.Blanking);          // breezeway
        }
        else
        {
            SetBurstRegion();                               // colour burst (rides on blanking)
        }
    }

    private void SetSimpleRegion(VideoRegion region)
    {
        _videoRegion = region;
        _videoAlternates = false;
        _videoConstantLevel = region == VideoRegion.SyncTip ? DacSyncLow : DacSyncHigh;
    }

    private void SetBurstRegion()
    {
        _videoRegion = VideoRegion.Burst;
        _videoAlternates = true;
        _videoHue = BurstHue;
        _videoLevelLow = DacBurstLow;
        _videoLevelHigh = DacBurstHigh;
    }

    private void SetActivePictureRegion(byte color)
    {
        _videoRegion = VideoRegion.ActivePicture;

        // LLHH: bits 0-3 hue code, bits 4-5 luma code. Colour emphasis ($2001
        // bits 5-7) is deliberately unmodelled - this is where the ~120-degree-
        // wide chroma pull-down for an emphasised sub-band would be applied.
        var hue = color & 0x0F;
        var luma = (color >> 4) & 0x03;

        switch (hue)
        {
            case 0x00:
                // Constant _h - grey, no chroma.
                _videoAlternates = false;
                _videoConstantLevel = DacLumaHigh[luma];
                break;

            case 0x0D:
                // Constant _l - dark grey.
                _videoAlternates = false;
                _videoConstantLevel = DacLumaLow[luma];
                break;

            case 0x0E:
            case 0x0F:
                // Blanking level - black.
                _videoAlternates = false;
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
