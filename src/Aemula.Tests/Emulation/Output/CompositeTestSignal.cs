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
//
// With colorBars it also encodes a 75% color-bar picture and the burst, the
// way each standard defines them: chroma is U sin(wt) + V cos(wt), with U and V
// weighted from the colour-difference signals, and the burst is -U (NTSC) or
// -U +/- V (PAL, the sign following the line's V switch). Everything here comes
// from those definitions, not from anything the decoder does, so a decoder and
// this encoder agreeing is evidence rather than two matching mistakes.
internal static class CompositeTestSignal
{
    // (R, G, B) of the seven bars, left to right: 100% white, then 75% colours.
    public static readonly (double R, double G, double B)[] Bars =
    [
        (1, 1, 1),
        (0.75, 0.75, 0),
        (0, 0.75, 0.75),
        (0, 0.75, 0),
        (0.75, 0, 0.75),
        (0.75, 0, 0),
        (0, 0, 0.75),
    ];

    // Burst peak amplitude as a fraction of the picture swing: PAL 150mV of
    // 700mV, NTSC 20 IRE of 100.
    private static double BurstAmplitude(TelevisionStandard standard) =>
        standard.AlternatesSecondAxis ? 0.15 / 0.7 : 0.2;

    // An arbitrary subcarrier phase at sample 0, so nothing depends on the
    // decoder's oscillator happening to start aligned.
    private const double InitialSubcarrierPhase = 0.37;

    private const double EqualizingPulseSeconds = 2.35e-6;

    // Half-line slots in each of the three vertical-blanking groups (2.5 lines
    // each on PAL, 3 on NTSC - 3 is used for both, which is within what the
    // decoder has to tolerate).
    private const int PulsesPerGroup = 6;
    private const int BlankLinesAfterSync = 20;

    public const byte WhiteByte = 224;

    public static byte BlankingByte(TelevisionStandard standard) =>
        (byte)Math.Round(WhiteByte / (1 + standard.ReferenceWhiteGainFromSyncSwing));

    public static byte[] Generate(TelevisionStandard standard, double samplesPerSecond, int fields, bool colorBars = false)
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

            if (colorBars && slot >= 3 * PulsesPerGroup)
            {
                value = AddColor(standard, value, n, halfLine, phase, halfLineSeconds, blanking);
            }

            samples[n] = value;
        }

        return samples;
    }

    // Replaces a flat picture/blanking sample with its colored equivalent:
    // the burst inside its window, a bar's luma plus chroma inside the picture.
    private static byte AddColor(
        TelevisionStandard standard,
        byte flat,
        int n,
        long halfLine,
        double phase,
        double halfLineSeconds,
        byte blanking)
    {
        var lineSeconds = (double)standard.LineSeconds;
        var linePhase = halfLine % 2 == 0 ? phase : phase + halfLineSeconds;
        var unit = WhiteByte - blanking;

        var carrier = Math.PI / 2 * n + InitialSubcarrierPhase;
        var sin = Math.Sin(carrier);
        var cos = Math.Cos(carrier);

        // PAL reverses V on alternate lines; NTSC never does.
        var vSign = standard.AlternatesSecondAxis && ((halfLine >> 1) & 1) == 1 ? -1.0 : 1.0;

        var burstStart = standard.HSyncWidthSeconds + standard.BurstStartSeconds;
        var burstEnd = burstStart + standard.BurstCycles / standard.ColorSubcarrierHz;
        if (linePhase >= burstStart && linePhase < burstEnd)
        {
            var burst = BurstAmplitude(standard);
            var component = standard.AlternatesSecondAxis ? burst / Math.Sqrt(2) : burst;
            var vTerm = standard.AlternatesSecondAxis ? vSign * component * cos : 0;
            return Clamp(blanking + unit * (-component * sin + vTerm));
        }

        var activeStart = 2.0 * standard.HSyncWidthSeconds;
        var activeEnd = lineSeconds - standard.FrontPorchSeconds;
        if (flat != WhiteByte || linePhase < activeStart || linePhase >= activeEnd)
        {
            return flat;
        }

        var bar = Math.Min(Bars.Length - 1, (int)((linePhase - activeStart) / (activeEnd - activeStart) * Bars.Length));
        var (r, g, b) = Bars[bar];
        var y = 0.299 * r + 0.587 * g + 0.114 * b;
        var u = 0.493 * (b - y);
        var v = 0.877 * (r - y);

        return Clamp(blanking + unit * (y + u * sin + vSign * v * cos));
    }

    private static byte Clamp(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
}
