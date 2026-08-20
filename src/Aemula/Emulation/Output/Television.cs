using System;
using Aemula.Emulation.Output.Ntsc;

namespace Aemula.Emulation.Output;

// See docs/television-plan.md. This class is the "front door" the rest of
// the codebase talks to: feed it one composite-video sample at a time via
// Decode, and it runs that sample through the whole decode pipeline built
// up over the earlier phases of the plan - sync separation (where's sync,
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
    private const double VerticalBlankingFastPulseRateFraction = 0.75;

    private readonly NtscSyncSeparator _syncSeparator = new();
    private readonly NtscRasterOscillators _rasterOscillators = new();
    private readonly NtscColorBurstPll _colorBurstPll = new();
    private readonly NtscYiqDecoder _yiqDecoder = new();

    // Seeded at the nominal NTSC frame shape so there's a sensible buffer
    // from sample 1, and resized in Decode below once the raster
    // oscillators' own measured timing (which can differ slightly per
    // signal - see docs/television-plan.md's "Raster oscillators" section)
    // is known.
    public readonly SampleBuffer SampleBuffer = new(
        (uint)Math.Round(NtscTiming.NominalSamplesPerLine),
        (uint)Math.Round(NtscTiming.NominalLinesPerField));

    // State behind _isVerticallyBlanked below - see UpdateVerticalBlanking.
    private bool _wasInSyncRegion;
    private double _samplesSincePulseStart = double.MaxValue;
    private bool _isVerticallyBlanked;

    // Hardcoded for now - see docs/television-plan.md's "Standard detection
    // seam". A real multi-standard TV works this out from the incoming
    // signal itself (line/frame rate, and PAL's line-to-line burst-phase
    // alternation), but this class doesn't have a PAL decode path to switch
    // to yet, so there's nothing to detect.
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
    public double ActiveVideoStartSamples => _syncSeparator.HSyncWidthEstimate;

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
    public double ActiveVideoLengthSamples =>
        DetectedSamplesPerLine
        - 2 * _syncSeparator.HSyncWidthEstimate
        - NtscTiming.NominalFrontPorchFraction * DetectedSamplesPerLine;

    /// <summary>
    /// The current running estimate of samples-per-line - see
    /// <see cref="NtscRasterOscillators"/>. Mainly useful for a status
    /// readout (e.g. TelevisionWindow's toolbar); the decode pipeline itself
    /// only ever needs <see cref="CurrentColumn"/>/<see cref="CurrentRow"/>.
    /// </summary>
    public double DetectedSamplesPerLine => _rasterOscillators.DetectedSamplesPerLine;

    /// <summary>
    /// The current running estimate of lines-per-frame - see
    /// <see cref="NtscRasterOscillators"/>. Same use as
    /// <see cref="DetectedSamplesPerLine"/>.
    /// </summary>
    public double DetectedLinesPerFrame => _rasterOscillators.DetectedLinesPerFrame;

    /// <summary>
    /// Whether a real color burst (as opposed to noise, or active-video
    /// content that happened to fall in the expected window) was found on
    /// the most recently completed line - see <see cref="NtscColorBurstPll"/>.
    /// A status readout's idea of "burst locked", not something the decode
    /// pipeline itself branches on.
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
    /// assumed to sample at exactly 4x the NTSC color subcarrier - see
    /// docs/television-plan.md's "Input signal contract".
    /// </summary>
    public void Decode(byte sample)
    {
        _syncSeparator.Process(sample);
        _rasterOscillators.Process(_syncSeparator.HSyncDetected, _syncSeparator.VSyncDetected);
        _colorBurstPll.Process(sample, _rasterOscillators.CurrentColumn, _syncSeparator.BlackLevel, _syncSeparator.WhiteLevel);
        _yiqDecoder.Process(sample, _colorBurstPll.PhaseOffsetRadians, _syncSeparator.BlackLevel, _syncSeparator.WhiteLevel);
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
        // just active video - so TelevisionWindow's Phase 7 full-raster view
        // has real pixels for its region overlays to sit over (see
        // docs/television-plan.md's "Output" section, which left this choice
        // open for Phase 7 to make). But only active video gets its full
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

    private static RgbaByte GrayscaleFromLuma(double luma)
    {
        var level = (byte)Math.Round(luma);
        return new RgbaByte(level, level, level, 255);
    }

    private void ResizeSampleBufferIfDetectedTimingChanged()
    {
        var width = (uint)Math.Round(_rasterOscillators.DetectedSamplesPerLine);
        var height = (uint)Math.Round(_rasterOscillators.DetectedLinesPerFrame);

        if (width != SampleBuffer.Width || height != SampleBuffer.Height)
        {
            SampleBuffer.Resize(width, height);
        }
    }
}
