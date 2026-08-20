using System;
using System.Diagnostics.CodeAnalysis;

namespace Aemula.Emulation.Output;

// Television's own per-sample output buffer - one Sample (color plus
// diagnostic context, see that struct) per raster position, resized in
// place (see Resize) whenever Television's detected timing changes, the
// same "resize on detected-timing-change" behavior the older, more generic
// Aemula.DisplayBuffer already has elsewhere in this codebase. Deliberately
// a separate type rather than reusing DisplayBuffer here - see
// docs/television-plan.md's Phase 7 - since this needs to carry per-sample
// data (Region today, more later - see Sample's own remarks) nothing else
// in this codebase's DisplayBuffer consumers (ScreenDisplayWindow,
// Atari2600's VideoOutput, etc.) needs or should have to know about.
public sealed class SampleBuffer
{
    public uint Width { get; private set; }
    public uint Height { get; private set; }

    public Sample[] Data { get; private set; }

    public SampleBuffer(uint width, uint height)
    {
        Resize(width, height);
    }

    // Preserves whatever was already decoded, at the same (row, column)
    // position, rather than wiping the buffer back to blank - a real TV's
    // own screen doesn't go dark the instant its horizontal/vertical
    // oscillators re-lock to a slightly different line/frame rate, it just
    // keeps showing the picture it already had (increasingly torn at the
    // edges the further the new timing drifts from the old, exactly what
    // this produces too - see the copy loop below). Detected timing changes
    // are frequent enough in normal operation (every sample re-evaluates
    // the raster oscillators' running estimate - see
    // Television.ResizeSampleBufferIfDetectedTimingChanged) that wiping the
    // whole picture on every one of them, as this used to, made this
    // window's display flicker to black far more than a real TV ever would.
    [MemberNotNull(nameof(Data))]
    public void Resize(uint width, uint height)
    {
        var oldData = Data;
        var oldWidth = Width;
        var oldHeight = Height;

        Width = width;
        Height = height;

        Data = new Sample[width * height];

        // Matches DisplayBuffer.Resize's own explicit opaque-black fill -
        // not every position in a freshly (re)sized buffer is guaranteed to
        // get written before something reads it back (e.g. right after a
        // detected-timing change, or before the very first line has
        // decoded), so this needs a sane, deliberate default rather than
        // whatever default(Sample) happens to be (Color fully transparent,
        // Region defaulting to whichever RasterRegion member happens to
        // be declared first - neither is a meaningful "nothing decoded here
        // yet" value). Positions the copy below actually overwrites get
        // this replaced immediately after; this fill still has to happen
        // first so every position not covered by the overlap (e.g. new rows
        // past the old buffer's height) ends up with the same sane default
        // it always did.
        for (var i = 0; i < Data.Length; i++)
        {
            Data[i] = new Sample
            {
                Color = new RgbaByte(0, 0, 0, 255),
                Region = RasterRegion.Blanking,
            };
        }

        // Copies row by row rather than as one flat block: Data is a
        // row-major flat array, so a row's worth of samples is contiguous,
        // but consecutive rows aren't adjacent across a width change (row R
        // starts at R*oldWidth in the old array but R*width in the new
        // one) - each row has to land at its own correctly-shifted offset.
        // Only the overlapping top-left rectangle (min of old/new width,
        // min of old/new height) has anywhere sane to go; a width/height
        // shrink simply drops whatever falls outside it, the same way a
        // real TV's picture would run off the edge of a suddenly-smaller
        // raster.
        if (oldData != null)
        {
            var copyWidth = Math.Min(oldWidth, width);
            var copyHeight = Math.Min(oldHeight, height);

            for (var row = 0; row < copyHeight; row++)
            {
                Array.Copy(oldData, row * oldWidth, Data, row * width, copyWidth);
            }
        }
    }
}
