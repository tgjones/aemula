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
// and writes the result into DisplayBuffer for something like
// TelevisionWindow to render.
public sealed class Television
{
    private readonly NtscSyncSeparator _syncSeparator = new();
    private readonly NtscRasterOscillators _rasterOscillators = new();
    private readonly NtscColorBurstPll _colorBurstPll = new();
    private readonly NtscYiqDecoder _yiqDecoder = new();

    // Seeded at the nominal NTSC frame shape so there's a sensible buffer
    // from sample 1, and resized in Decode below once the raster
    // oscillators' own measured timing (which can differ slightly per
    // signal - see docs/television-plan.md's "Raster oscillators" section)
    // is known.
    public readonly DisplayBuffer DisplayBuffer = new(
        (uint)Math.Round(NtscTiming.NominalSamplesPerLine),
        (uint)Math.Round(NtscTiming.NominalLinesPerField));

    // Hardcoded for now - see docs/television-plan.md's "Standard detection
    // seam". A real multi-standard TV works this out from the incoming
    // signal itself (line/frame rate, and PAL's line-to-line burst-phase
    // alternation), but this class doesn't have a PAL decode path to switch
    // to yet, so there's nothing to detect.
    public TelevisionStandard Standard => TelevisionStandard.Ntsc;

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
    /// porch - as opposed to sync/blanking. This does not (yet - see
    /// docs/television-plan.md's Phase 7) account for vertical blanking, so
    /// a sample can be "active" by this definition while still on a
    /// vertical-blanking line.
    /// </summary>
    public bool IsActiveVideo =>
        CurrentColumn >= NtscTiming.ActiveVideoStartSamples
        && CurrentColumn < NtscTiming.ActiveVideoStartSamples + NtscTiming.ActiveVideoLengthSamples;

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

        ResizeDisplayBufferIfDetectedTimingChanged();

        if (!IsActiveVideo)
        {
            return;
        }

        var column = CurrentColumn - (int)NtscTiming.ActiveVideoStartSamples;
        var row = CurrentRow;

        // CurrentColumn/CurrentRow come from the raster oscillators' own
        // live, continuously-adjusting period estimates (see
        // NtscRasterOscillators), while DisplayBuffer's dimensions are only
        // ever the last *rounded snapshot* of those same estimates - so
        // right after a real signal's timing shifts (or while sync is still
        // flywheeling, unlocked - see the raster oscillators'
        // capture-range/flywheel behavior), a position can transiently fall
        // just outside the current buffer. Expected, not a bug - simply
        // dropped rather than throwing.
        if (column < 0 || column >= DisplayBuffer.Width || row < 0 || row >= DisplayBuffer.Height)
        {
            return;
        }

        DisplayBuffer.Data[row * DisplayBuffer.Width + column] = _yiqDecoder.Rgb;
    }

    private void ResizeDisplayBufferIfDetectedTimingChanged()
    {
        var width = (uint)Math.Round(_rasterOscillators.DetectedSamplesPerLine);
        var height = (uint)Math.Round(_rasterOscillators.DetectedLinesPerFrame);

        if (width != DisplayBuffer.Width || height != DisplayBuffer.Height)
        {
            DisplayBuffer.Resize(width, height);
        }
    }
}
