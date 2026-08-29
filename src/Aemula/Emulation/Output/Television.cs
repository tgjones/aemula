using System;
using Aemula.Emulation.Output.Ntsc;

namespace Aemula.Emulation.Output;

// This class is the "front door" the rest of the codebase talks to: feed it
// one composite-video sample at a time via Decode, and it runs that sample
// through the whole decode pipeline - sync separation (where's sync,
// black, white?), raster oscillators (where in the picture is this sample?),
// the color-burst PLL (what phase is the color subcarrier at right now?),
// and finally the YIQ decoder (turn this sample into an actual RGB pixel) -
// and writes the result into SampleBuffer for something like TelevisionWindow
// to render.
public sealed class Television
{
    // How short a gap between two consecutive confirmed sync pulses'
    // *starts* counts as "still within a fast-recurring pulse train" (real
    // NTSC's vertical-blanking equalizing/serration pulses, which recur at
    // roughly twice the normal per-line HSYNC rate) rather than "a normal
    // once-per-line HSYNC" - see _isVerticallyBlanked's remarks. Chosen to
    // sit between the two real rates (~0.5 line for equalizing/serration
    // pulses, 1.0 line for normal HSYNC) with comfortable margin on both
    // sides, and confirmed empirically against both smpte.ntsc and a
    // booted AppleIISystem's real vertical-blanking pulse timing - not a
    // value picked from spec alone, the same "expect to tune against real
    // signals" spirit as this decoder's other engineering-margin constants
    // (see e.g. NtscSyncSeparator's HSyncToleranceLowerFraction/
    // VSyncWidthMultiplier).
    private const float VerticalBlankingFastPulseRateFraction = 0.75f;

    private readonly NtscSyncSeparator _syncSeparator = new();
    private readonly NtscRasterOscillators _rasterOscillators = new();
    private readonly NtscColorBurstPll _colorBurstPll = new();
    private readonly NtscYiqDecoder _yiqDecoder = new();

    // Seeded at the nominal NTSC frame shape so there's a sensible buffer
    // from sample 1, and resized in Decode below once the raster
    // oscillators' own measured timing (which can differ slightly per
    // signal) is known.
    public readonly SampleBuffer SampleBuffer = new(
        (uint)MathF.Round(NtscTiming.NominalSamplesPerLine),
        (uint)MathF.Round(NtscTiming.NominalLinesPerField));

    // State behind _isVerticallyBlanked below - see UpdateVerticalBlanking.
    private bool _wasInSyncRegion;
    private float _samplesSincePulseStart = float.MaxValue;
    private bool _isVerticallyBlanked;

    // Hardcoded for now. A real multi-standard TV works this out from the
    // incoming signal itself (line/frame rate, and PAL's line-to-line
    // burst-phase alternation), but this class doesn't have a PAL decode
    // path to switch to yet, so there's nothing to detect.
    public TelevisionStandard Standard => TelevisionStandard.Ntsc;

    /// <summary>
    /// Where active video starts within a line, in samples, measured from
    /// HSYNC's trailing edge (i.e. <see cref="CurrentColumn"/> == 0) - see
    /// <see cref="IsActiveVideo"/>. Self-calibrated from
    /// <see cref="NtscSyncSeparator.HSyncWidthEstimate"/> rather than a
    /// fixed nominal sample count: RS-170A defines this gap as the same
    /// duration as HSYNC's own pulse (NtscTiming's ActiveVideoStartSamples
    /// and NominalHSyncWidthSamples constants are literally the same
    /// formula), and NtscSyncSeparator already tracks a real, self-
    /// calibrated HSYNC width for this exact signal - reusing it here means
    /// this tracks the real signal's own timing (e.g. Apple II's actual
    /// back-porch width, whatever it really is) instead of assuming nominal
    /// spec, the same way DetectedSamplesPerLine already does for line
    /// length instead of assuming NtscTiming.NominalSamplesPerLine.
    /// </summary>
    public float ActiveVideoStartSamples => _syncSeparator.HSyncWidthEstimate;

    /// <summary>
    /// The active-video portion of one scanline, in samples - see
    /// <see cref="IsActiveVideo"/>. Self-calibrated: front porch (the one
    /// remaining unknown) has no detectable signal feature of its own -
    /// same reason NtscColorBurstPll's burst window position isn't self-
    /// calibrated either - so it's kept as a fixed *proportion* of a
    /// nominal line (<see cref="NtscTiming.NominalFrontPorchFraction"/>),
    /// but applied to <see cref="DetectedSamplesPerLine"/> rather than
    /// baked in as an absolute sample count, so this still scales
    /// correctly if a real signal's line length differs from nominal (as
    /// Apple II's real ~912-vs-909.3 samples/line already does).
    /// </summary>
    public float ActiveVideoLengthSamples =>
        DetectedSamplesPerLine
        - 2 * _syncSeparator.HSyncWidthEstimate
        - NtscTiming.NominalFrontPorchFraction * DetectedSamplesPerLine;

    /// <summary>
    /// The current running estimate of samples-per-line - see
    /// <see cref="NtscRasterOscillators"/>. Mainly useful for a status
    /// readout (e.g. TelevisionWindow's toolbar); the decode pipeline itself
    /// only ever needs <see cref="CurrentColumn"/>/<see cref="CurrentRow"/>.
    /// </summary>
    public float DetectedSamplesPerLine => _rasterOscillators.DetectedSamplesPerLine;

    /// <summary>
    /// The current running estimate of lines-per-frame - see
    /// <see cref="NtscRasterOscillators"/>. Same use as
    /// <see cref="DetectedSamplesPerLine"/>.
    /// </summary>
    public float DetectedLinesPerFrame => _rasterOscillators.DetectedLinesPerFrame;

    /// <summary>
    /// Whether a real color burst (as opposed to noise, or active-video
    /// content that happened to fall in the expected window) was found on
    /// the most recently completed line - see <see cref="NtscColorBurstPll"/>.
    /// The decode pipeline branches on this too: a line with no detected
    /// burst is decoded as grayscale, the same as a real receiver's color
    /// killer (see <see cref="NtscYiqDecoder.Process"/>).
    /// </summary>
    public bool ColorBurstLocked => _colorBurstPll.BurstDetected;

    /// <summary>
    /// The raster column (sample position within the current line) of the
    /// sample most recently passed to <see cref="Decode"/>.
    /// </summary>
    public int CurrentColumn => _rasterOscillators.CurrentColumn;

    /// <summary>
    /// The raster row (line position within the current field) of the
    /// sample most recently passed to <see cref="Decode"/>.
    /// </summary>
    public int CurrentRow => _rasterOscillators.CurrentRow;

    /// <summary>
    /// Whether the sample most recently passed to <see cref="Decode"/> fell
    /// within the horizontally-visible part of the line - past the sync
    /// pulse, breezeway, color burst, and back porch, and before the front
    /// porch - as opposed to sync/blanking. This is purely a horizontal,
    /// per-line column check - it says nothing about vertical blanking (see
    /// <see cref="ClassifyCurrentSample"/>'s own separate check for that) -
    /// so a sample can be "active" by this definition while still on a
    /// vertical-blanking line; <see cref="Sample.Region"/> (stored in
    /// <see cref="SampleBuffer"/> for every decoded sample) is the
    /// classification that accounts for both.
    /// </summary>
    public bool IsActiveVideo =>
        CurrentColumn >= ActiveVideoStartSamples
        && CurrentColumn < ActiveVideoStartSamples + ActiveVideoLengthSamples;

    /// <summary>
    /// Feeds one composite-video sample into the decoder. Every caller is
    /// assumed to sample at exactly 4x the NTSC color subcarrier.
    /// </summary>
    // The vertical counterpart to ActiveVideoStartSamples/ActiveVideoLengthSamples -
    // unlike those, this doesn't need its own self-calibrated formula, because
    // ClassifyCurrentSample's live vertical-blanking check (see that method's
    // remarks) already makes Sample.Region trustworthy vertically as well as
    // horizontally, so this can just read Region straight out of SampleBuffer
    // rather than reconstructing timing separately. Finds the longest
    // contiguous block of rows whose ActiveVideo sample count clears half of
    // ActiveVideoLengthSamples - comfortably separates full picture rows (the
    // whole ActiveVideoLengthSamples-worth) from blanking rows (0, or a
    // partial, self-correcting count right at a vertical-blanking region's
    // edge - see this class's own remarks on why that edge case exists and is
    // acceptable).
    //
    // Shared by TelevisionWindow (which needs it every UI frame, for both its
    // "active video only" crop and its vertical-stretch aspect-ratio math) and
    // any other consumer that wants the same "which rows are really picture"
    // answer - one implementation instead of each caller re-deriving it and
    // risking drift.
    public (int StartRow, int RowCount) ComputeActiveVideoRowRange()
    {
        var width = (int)SampleBuffer.Width;
        var height = (int)SampleBuffer.Height;
        if (width <= 0 || height <= 0)
        {
            return (0, height);
        }

        var samples = SampleBuffer.Data;
        var activeThreshold = ActiveVideoLengthSamples * 0.5f;

        var bestStart = 0;
        var bestCount = 0;
        var runStart = -1;

        for (var row = 0; row <= height; row++)
        {
            var isActiveRow = false;
            if (row < height)
            {
                var activeCount = 0;
                var rowOffset = row * width;
                for (var column = 0; column < width; column++)
                {
                    if (samples[rowOffset + column].Region == RasterRegion.ActiveVideo)
                    {
                        activeCount++;
                    }
                }
                isActiveRow = activeCount > activeThreshold;
            }

            if (isActiveRow)
            {
                if (runStart < 0)
                {
                    runStart = row;
                }
            }
            else if (runStart >= 0)
            {
                var runCount = row - runStart;
                if (runCount > bestCount)
                {
                    bestStart = runStart;
                    bestCount = runCount;
                }
                runStart = -1;
            }
        }

        return bestCount > 0 ? (bestStart, bestCount) : (0, height);
    }

    // A real broadcast picture's active area is conventionally 4:3, but
    // SampleBuffer is one raw sample per column and one scanline per row -
    // absolutely not the same physical size as each other, since this
    // decoder's horizontal sampling rate packs a scanline's active-video
    // samples (ActiveVideoLengthSamples) into the same physical width a real
    // set devotes to a whole 4:3-shaped picture only activeLineCount lines
    // tall (the detected vertical active-line count - see
    // ComputeActiveVideoRowRange). Rendered at native 1 sample:1 line
    // square-pixel scaling, this comes out badly squashed into a thin
    // horizontal band instead of anything resembling a picture. This factor
    // is purely a display-time correction (the same "non-square pixel"
    // adjustment real video tooling applies when showing a broadcast-format
    // capture on a square-pixel screen) - it's for a consumer to scale how
    // large it draws the picture, not something that touches SampleBuffer's
    // actual data, which stays at native sample/line resolution for region
    // overlays (and any other consumer that needs raw positions).
    //
    // Takes activeLineCount as a parameter rather than calling
    // ComputeActiveVideoRowRange itself - a caller that also needs the row
    // range this frame (e.g. TelevisionWindow, for its crop) gets both from
    // one scan of SampleBuffer instead of two.
    public float ComputeVerticalStretchFactor(float activeLineCount) =>
        (ActiveVideoLengthSamples / activeLineCount) / (4f / 3f);

    public void Decode(byte sample)
    {
        _syncSeparator.Process(sample);
        _rasterOscillators.Process(_syncSeparator.HSyncDetected, _syncSeparator.VSyncDetected);

        // Decode gain is anchored to a reference white reconstructed from
        // the sync tip and blanking levels the signal always carries, not
        // to NtscSyncSeparator's running picture-peak _whiteLevel: a dim
        // scene (Pitfall's forest, a night sky) may never contain reference
        // white at all, and a running-max AGC then inflates the gain and
        // blows out every colour. This mirrors a real receiver's gated-sync
        // AGC, which keys off the sync interval, never off picture white.
        // NtscSyncSeparator still tracks its running _whiteLevel for the
        // WhiteLevel status readout, but nothing in decode consumes it now.
        var whiteRef = NtscYiqDecoder.WhiteReference(_syncSeparator.BlackLevel, _syncSeparator.SyncLevel);
        _colorBurstPll.Process(sample, _rasterOscillators.CurrentColumn, _syncSeparator.BlackLevel, whiteRef);
        _yiqDecoder.Process(sample, _colorBurstPll.PhaseOffsetRadians, _syncSeparator.BlackLevel, _syncSeparator.SyncLevel, _colorBurstPll.BurstDetected);
        UpdateVerticalBlanking();

        ResizeSampleBufferIfDetectedTimingChanged();

        var column = CurrentColumn;
        var row = CurrentRow;

        // CurrentColumn/CurrentRow come from the raster oscillators' own
        // live, continuously-adjusting period estimates (see
        // NtscRasterOscillators), while SampleBuffer's dimensions are only
        // ever the last *rounded snapshot* of those same estimates - so
        // right after a real signal's timing shifts (or while sync is still
        // flywheeling, unlocked - see the raster oscillators'
        // capture-range/flywheel behavior), a position can transiently fall
        // just outside the current buffer. Expected, not a bug - simply
        // dropped rather than throwing.
        if (column < 0 || column >= SampleBuffer.Width || row < 0 || row >= SampleBuffer.Height)
        {
            return;
        }

        var region = ClassifyCurrentSample();

        // Every sample is written here, at its true raster position - not
        // just active video - so TelevisionWindow's full-raster view has
        // real pixels for its region overlays to sit over. But only active
        // video gets its full
        // decoded *color* - sync/blanking/color-burst samples decode to real
        // but meaningless chroma (NtscYiqDecoder's own remarks), and writing
        // that as-is would paint color burst's own reference-phase flicker
        // as a spurious, wrongly-hued stripe. Luma alone (I = Q = 0, i.e.
        // plain grayscale) is still a faithful *brightness* reading for
        // those samples - and for sync/blanking, whose sample value barely
        // deviates from black/sync level, that comes out close to black
        // anyway, same as this buffer's un-written background used to be.
        SampleBuffer.Data[row * SampleBuffer.Width + column] = new Sample
        {
            Region = region,
            Color = region == RasterRegion.ActiveVideo
                ? _yiqDecoder.Rgb
                : GrayscaleFromLuma(_yiqDecoder.Luma),
            RawSample = sample,
            CarrierPhaseRadians = _colorBurstPll.CurrentPhaseRadians,
            Luma = _yiqDecoder.Luma,
            I = _yiqDecoder.I,
            Q = _yiqDecoder.Q,
        };
    }

    // Which part of the signal produced the sample just processed - read
    // straight from the same live state each earlier pipeline stage already
    // computed for its own reasons, not a separate reconstruction from
    // nominal timing (an earlier version of this worked that way, computed
    // after the fact from NtscTiming's fixed windows and NtscRasterOscillators'
    // detected line length, and was deliberately replaced - see
    // RasterRegion's remarks). In priority order: NtscSyncSeparator's own
    // live, self-calibrated pulse-width classification (HSYNC vs. VSYNC) if
    // this sample is part of a sync pulse at all; otherwise NtscColorBurstPll's
    // own live burst-window flag (burst can legitimately still occur on a
    // vertical-blanking line, and keeps priority the same way it already
    // does within a normal line); otherwise _isVerticallyBlanked (see
    // UpdateVerticalBlanking) if we're currently within a vertical-blanking
    // pulse train; otherwise IsActiveVideo, the same flag used above that
    // decides whether this sample's Color gets full YIQ color or plain
    // grayscale.
    private RasterRegion ClassifyCurrentSample()
    {
        if (_syncSeparator.CurrentSyncRegion is { } syncRegion)
        {
            return syncRegion;
        }

        if (_colorBurstPll.IsInBurstWindow)
        {
            return RasterRegion.ColorBurst;
        }

        if (_isVerticallyBlanked)
        {
            return RasterRegion.Blanking;
        }

        return IsActiveVideo ? RasterRegion.ActiveVideo : RasterRegion.Blanking;
    }

    // Live vertical-blanking detection, modeled as a real vertical-sync
    // separator circuit actually works: a retriggerable timer, not a fixed
    // window. Every time a new sync pulse starts (CurrentSyncRegion
    // transitions from null to non-null), the gap since the *previous*
    // pulse's start reveals which kind of pulse train we're in - real
    // NTSC's vertical-blanking equalizing/serration pulses recur at
    // roughly twice the normal per-line HSYNC rate, so a short gap means
    // "still in vertical blanking" and a normal (one-line) gap means "back
    // to normal HSYNC, i.e. real picture". This asserts (or clears)
    // _isVerticallyBlanked right at that pulse's own edge, then holds it
    // until the next pulse re-evaluates it - not a per-sample decay, since
    // there's nothing to decay against between pulses.
    //
    // Horizontal blanking doesn't need an equivalent of this: its own
    // "which part of the line is this" question is already answered by
    // IsActiveVideo's self-calibrated column window (see that property's
    // remarks) precisely because a line has only one HSYNC pulse to key
    // off of - there's no pulse-recurrence-rate signal available
    // horizontally the way there is vertically.
    //
    // An earlier version of this tried classifying each row's own pulse
    // count directly (>=2 confirmed pulses so far this row -> blanking),
    // live and causal - it was too weak in practice: real vertical-blanking
    // rows' pulses often land late enough in the row that most of it had
    // already been mis-written as ActiveVideo before the 2nd pulse ever
    // arrived to correct it. The retriggerable-timer model above was
    // verified against real per-sample data from both smpte.ntsc and a
    // booted AppleIISystem before landing on it: genuine blanking rows
    // correctly drop to (almost) no ActiveVideo samples, genuine picture
    // rows are unaffected, and only the single row on each edge of a
    // vertical-blanking region shows a partial, self-correcting result -
    // the same kind of "briefly wrong right at a pulse's own edge, then
    // corrects" behavior NtscSyncSeparator.CurrentSyncRegion's own remarks
    // already describe as acceptable for live, causal classification.
    private void UpdateVerticalBlanking()
    {
        var inSyncRegionNow = _syncSeparator.CurrentSyncRegion != null;

        if (inSyncRegionNow && !_wasInSyncRegion)
        {
            var fastPulseRateThreshold = VerticalBlankingFastPulseRateFraction * DetectedSamplesPerLine;
            _isVerticallyBlanked = _samplesSincePulseStart < fastPulseRateThreshold;
            _samplesSincePulseStart = 0;
        }
        else
        {
            _samplesSincePulseStart++;
        }

        _wasInSyncRegion = inSyncRegionNow;
    }

    private static RgbaByte GrayscaleFromLuma(float luma)
    {
        var level = (byte)MathF.Round(luma);
        return new RgbaByte(level, level, level, 255);
    }

    // A detected dimension only re-sizes the buffer once it has moved a full
    // sample/line clear of the current buffer's dimension - not the instant
    // MathF.Round of it changes. Without this deadband, a signal whose
    // detected geometry sits right on a rounding boundary re-allocates the
    // whole (LOH-sized) Sample[] every few frames just from the estimate
    // dithering across .5: Space Invaders' DetectedLinesPerFrame is ~260.5,
    // and its deliberately-unserrated VSYNC (see
    // SpaceInvadersSystem.CompositeVideo.cs) keeps the vertical oscillator
    // hunting, so the height flipped 260<->261 continuously - ~6 MB of LOH
    // churn per two frames. The buffer being up to a line/sample off the
    // latest estimate is already a handled case (Decode drops out-of-bounds
    // samples; Resize leaves uncovered rows opaque black).
    private const float ResizeDeadband = 1.0f;

    private void ResizeSampleBufferIfDetectedTimingChanged()
    {
        var detectedWidth = _rasterOscillators.DetectedSamplesPerLine;
        var detectedHeight = _rasterOscillators.DetectedLinesPerFrame;

        if (MathF.Abs(detectedWidth - SampleBuffer.Width) > ResizeDeadband ||
            MathF.Abs(detectedHeight - SampleBuffer.Height) > ResizeDeadband)
        {
            SampleBuffer.Resize(
                (uint)MathF.Round(detectedWidth),
                (uint)MathF.Round(detectedHeight));
        }
    }
}
