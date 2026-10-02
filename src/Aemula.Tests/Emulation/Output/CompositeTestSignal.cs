using System;
using Aemula.Emulation.Output;

namespace Aemula.Tests.Emulation.Output;

// A synthetic monochrome composite signal for either standard at any sample
// rate: interlaced fields with the vertical-blanking pulse train (equalizing
// pulses, then serrated broad pulses, then equalizing pulses again), normal
// HSYNC on every line after it, a run of picture-less lines, then a flat white
// picture. Built from the standard's own physical timing (seconds), not from
// the decoder's resolved sample counts, so it is an independent description of
// the signal the decoder has to recover.
internal static class CompositeTestSignal
{
    private const double EqualizingPulseSeconds = 2.35e-6;

    // Half-line slots in each of the three vertical-blanking groups (2.5 lines
    // each on PAL, 3 on NTSC - 3 is used for both, which is within what the
    // decoder has to tolerate).
    private const int PulsesPerGroup = 6;
    private const int BlankLinesAfterSync = 20;

    public const byte WhiteByte = 224;

    public static byte BlankingByte(TelevisionStandard standard) =>
        (byte)Math.Round(WhiteByte / (1 + standard.ReferenceWhiteGainFromSyncSwing));

    public static byte[] Generate(TelevisionStandard standard, double samplesPerSecond, int fields)
    {
        var lineSeconds = (double)standard.LineSeconds;
        var halfLineSeconds = lineSeconds / 2;
        var hSyncSeconds = (double)standard.HSyncWidthSeconds;
        var halfLinesPerField = (int)Math.Round(standard.LinesPerField * 2);
        var blanking = BlankingByte(standard);

        var activeStartSeconds = 2 * hSyncSeconds;
        var activeEndSeconds = lineSeconds - standard.FrontPorchSeconds;

        var sampleCount = (int)(fields * halfLinesPerField * halfLineSeconds * samplesPerSecond);
        var samples = new byte[sampleCount];

        for (var n = 0; n < sampleCount; n++)
        {
            var t = n / samplesPerSecond;
            var halfLine = (long)Math.Floor(t / halfLineSeconds);
            var phase = t - halfLine * halfLineSeconds;
            var slot = (int)(halfLine % halfLinesPerField);

            byte value;
            if (slot < 3 * PulsesPerGroup)
            {
                var broad = slot >= PulsesPerGroup && slot < 2 * PulsesPerGroup;
                var pulseSeconds = broad ? halfLineSeconds - hSyncSeconds : EqualizingPulseSeconds;
                value = phase < pulseSeconds ? (byte)0 : blanking;
            }
            else if (halfLine % 2 == 0 && phase < hSyncSeconds)
            {
                value = 0;
            }
            else
            {
                var linePhase = halfLine % 2 == 0 ? phase : phase + halfLineSeconds;
                var linesIntoPicture = (slot - 3 * PulsesPerGroup) / 2;
                var inPicture = linesIntoPicture >= BlankLinesAfterSync
                    && linePhase >= activeStartSeconds
                    && linePhase < activeEndSeconds;
                value = inPicture ? WhiteByte : blanking;
            }

            samples[n] = value;
        }

        return samples;
    }
}
