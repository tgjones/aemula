using System;
using Aemula.Emulation.Output;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Aemula.Console;

public static class ScreenshotWriter
{
    // Same crop (active video only) and vertical stretch (4:3, see
    // Television.ComputeVerticalStretchFactor's own remarks) TelevisionWindow
    // applies by default - this is "what a real TV would show", which is what
    // matters for eyeballing whether a headless run's output looks right.
    public static void Write(Television television, string path)
    {
        var (verticalActiveStart, verticalActiveCount) = television.ComputeActiveVideoRowRange();
        var stretchFactor = television.ComputeVerticalStretchFactor(verticalActiveCount);

        var width = (int)television.ActiveVideoLengthSamples;
        var height = (int)MathF.Round(verticalActiveCount * stretchFactor);

        using var image = new Image<Rgba32>(width, height);

        var samples = television.SampleBuffer.Data;
        var sampleBufferWidth = (int)television.SampleBuffer.Width;
        var activeStartColumn = (int)television.ActiveVideoStartSamples;

        image.ProcessPixelRows(accessor =>
        {
            for (var outputRow = 0; outputRow < height; outputRow++)
            {
                // Nearest-neighbor row mapping only - this is a diagnostic
                // screenshot, not a precision resample, and the stretch factor
                // is rarely a clean integer ratio so there's no exact mapping
                // to have anyway.
                var sourceRow = verticalActiveStart + (int)(outputRow / stretchFactor);
                if (sourceRow >= verticalActiveStart + verticalActiveCount)
                {
                    sourceRow = verticalActiveStart + verticalActiveCount - 1;
                }

                var rowSpan = accessor.GetRowSpan(outputRow);
                var sourceRowOffset = sourceRow * sampleBufferWidth + activeStartColumn;

                for (var column = 0; column < width; column++)
                {
                    var color = samples[sourceRowOffset + column].Color;
                    rowSpan[column] = new Rgba32(color.R, color.G, color.B, color.A);
                }
            }
        });

        image.SaveAsPng(path);
    }
}
