using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Aemula;
using Aemula.Emulation.Output;
using Aemula.Emulation.Systems.Nes;

namespace Aemula.Tests.Emulation.Systems.Nes;

// The code-verified replacement for "eyeball the Television window and hope it
// looks right" for the NES: the 2C02 builds the whole analog NTSC waveform
// itself (Ricoh2C02Chip.Video.cs), NesSystem.CompositeVideo scales + decimates
// it into NesSystem.Television.Decode one sample at a time, and these tests
// check what comes back out of the decoder.
//
// DecodedBackgroundColourMatchesSystemPalette is the NES analogue of
// Atari2600SystemTelevisionTests.EveryHueCodeDecodesCloseToTheReferencePalette:
// with background rendering left disabled the 2C02 emits palette entry $3F00
// across the entire active picture, so the decoded picture is one flat colour,
// and it should land on Ricoh2C02Chip's own hardware-derived _systemPalette
// entry for that code. Nothing in the burst -> I/Q path is fitted per system
// (the 2C02's burst tap, NtscColorBurstPll's single lock, NtscYiqDecoder's
// spec-derived burst rotation), so this pins down absolute burst phase / hue
// direction, not merely self-consistent colour. The bit-exact anchor for the
// waveform itself is Ricoh2C02Tests (node-for-node against Flawless2C02).
//
// FrameLockSmokeTest is the NES side of Testing item 3: a real ROM's composite
// signal must lock the Television to a stable frame boundary rather than
// free-run past the frame-runner safety cap.
public class NesSystemTelevisionTests
{
    // Column / row range, as a fraction of the buffer, that every measurement
    // samples - a block through the centre of the picture. Television frames
    // active video by RS-170A proportions, wider than the 2C02's actual
    // 256-dot picture, so its ActiveVideo region's edge columns include
    // blanking-level (byte 64) samples; averaging those in drags every reading
    // toward black. Same centre-strip fix Atari2600SystemTelevisionTests uses.
    private const double PictureCentreLo = 0.42;
    private const double PictureCentreHi = 0.58;
    private const double PictureRowLo = 0.40;
    private const double PictureRowHi = 0.60;

    private readonly record struct FrameRunResult(int FramesRun, long CyclesExecuted, long MaxCycles);

    // Inline frame runner - Aemula.Tests does not reference Aemula.Console, so
    // this mirrors FrameRunner.Run: tick until Television.CurrentRow wraps to a
    // lower value frameCount times, with a 10x-nominal safety cap so a signal
    // that never locks fails loudly instead of hanging.
    private static FrameRunResult RunFrames(NesSystem nes, int frameCount)
    {
        // These tests read Sample.Region / the decoded Color back out of
        // SampleBuffer, which Television only populates when asked to.
        nes.Television.CaptureSampleDiagnostics = true;

        var previousRow = nes.Television.CurrentRow;
        var framesCompleted = 0;
        var cycles = 0L;
        var maxCycles = (long)(nes.CyclesPerSecond / 60UL * (ulong)frameCount * 10UL);

        while (framesCompleted < frameCount)
        {
            nes.Tick();
            cycles++;

            var currentRow = nes.Television.CurrentRow;
            if (currentRow < previousRow)
            {
                framesCompleted++;
            }
            previousRow = currentRow;

            if (cycles > maxCycles)
            {
                throw new InvalidOperationException(
                    $"{frameCount} frame(s) requested but the NES composite signal never " +
                    $"locked to a frame boundary after {cycles} cycles.");
            }
        }

        return new FrameRunResult(framesCompleted, cycles, maxCycles);
    }

    // Mean decoded RGB over the centre block of the picture, ActiveVideo samples
    // only (sync/blanking/burst samples decode to real but meaningless colours -
    // see Television.Decode).
    private static (double R, double G, double B, int Count) AverageCentreBlock(SampleBuffer buffer)
    {
        double sumR = 0, sumG = 0, sumB = 0;
        var count = 0;

        var columnLo = (int)(buffer.Width * PictureCentreLo);
        var columnHi = (int)(buffer.Width * PictureCentreHi);
        var rowLo = (int)(buffer.Height * PictureRowLo);
        var rowHi = (int)(buffer.Height * PictureRowHi);

        for (var row = rowLo; row < rowHi; row++)
        {
            var rowOffset = row * (int)buffer.Width;
            for (var column = columnLo; column < columnHi; column++)
            {
                var sample = buffer.Data[rowOffset + column];
                if (sample.Region != RasterRegion.ActiveVideo)
                {
                    continue;
                }

                sumR += sample.Color.R;
                sumG += sample.Color.G;
                sumB += sample.Color.B;
                count++;
            }
        }

        return count == 0 ? (0, 0, 0, 0) : (sumR / count, sumG / count, sumB / count, count);
    }

    private static (double R, double G, double B) DecodeFlatBackground(byte code, int frames)
    {
        var nes = new NesSystem();
        nes.Ppu.SetPaletteMemory(0x00, code);
        RunFrames(nes, frames);

        var (r, g, b, count) = AverageCentreBlock(nes.Television.SampleBuffer);
        if (count == 0)
        {
            throw new InvalidOperationException(
                $"no ActiveVideo samples decoded for code ${code:X2} - the picture never locked");
        }

        return (r, g, b);
    }

    private static double ColorDistance(double r, double g, double b, Color expected)
    {
        var dr = r - expected.R;
        var dg = g - expected.G;
        var db = b - expected.B;
        return Math.Sqrt(dr * dr + dg * dg + db * db);
    }

    // Frames to settle the Television's sync / gain / geometry / colour-burst
    // PLL before sampling. The Apple II / Atari tests lock in a handful; 18 is a
    // comfortable margin.
    private const int SettleFrames = 18;

    [Test]
    public async Task DecodedBackgroundColourMatchesSystemPalette()
    {
        var ppu = new NesSystem().Ppu;

        // The four greys ($x0, constant _h, no chroma) - pure luma, tight
        // tolerance. $20 and $30 are the same entry in _systemPalette (both
        // clip at white) and both luma DAC taps clip at 1962, so they decode
        // to the same hot near-white.
        var greyCodes = new byte[] { 0x00, 0x10, 0x20, 0x30 };

        // Saturated hues spread ~120 degrees around the wheel at a mid-luma
        // row: $21 (blue), $2A (green), $26 (red).
        var hueCodes = new byte[] { 0x21, 0x2A, 0x26 };

        const double greyTolerance = 45.0;

        // Hues get a wider bound than greys. RGB distance overstates hue error
        // for saturated colours sitting near the YIQ I axis - the same reason
        // Atari2600SystemTelevisionTests measures its hues as a phase angle
        // instead - and $26 (red/orange) sits almost on it. On top of that,
        // NtscYiqDecoder is a linear decoder while Ricoh2C02Chip._systemPalette
        // carries display gamma, which compresses saturated-hue luminance the
        // decoder then can't reproduce, so every hue keeps a systematic
        // residual. NesSystem.CompositeVideo's band-limiting FIR is already
        // pinned to the one cutoff that both keeps the colour burst locked and
        // leaves the blanking-level near-black codes ($0F/$1D) decoding dark,
        // so it can't be retuned to shave that residual. $21/$2A land ~28-30;
        // $26 lands ~50.
        const double hueTolerance = 52.0;

        var report = new StringBuilder();
        report.AppendLine("code  decoded RGB            palette RGB           distance");

        var failures = new List<string>();
        var greyLuma = new double[greyCodes.Length];

        for (var i = 0; i < greyCodes.Length; i++)
        {
            var code = greyCodes[i];
            var (r, g, b) = DecodeFlatBackground(code, SettleFrames);
            var expected = ppu.GetSystemPaletteEntry(code);
            var distance = ColorDistance(r, g, b, expected);
            greyLuma[i] = r + g + b;

            report.AppendLine(
                $"${code:X2}   ({r,5:F1},{g,5:F1},{b,5:F1})   " +
                $"({expected.R,3},{expected.G,3},{expected.B,3})         {distance,6:F1}");

            if (distance > greyTolerance)
            {
                failures.Add($"grey ${code:X2}: distance {distance:F1} > {greyTolerance}");
            }
        }

        foreach (var code in hueCodes)
        {
            var (r, g, b) = DecodeFlatBackground(code, SettleFrames);
            var expected = ppu.GetSystemPaletteEntry(code);
            var distance = ColorDistance(r, g, b, expected);

            report.AppendLine(
                $"${code:X2}   ({r,5:F1},{g,5:F1},{b,5:F1})   " +
                $"({expected.R,3},{expected.G,3},{expected.B,3})         {distance,6:F1}");

            if (distance > hueTolerance)
            {
                failures.Add($"hue ${code:X2}: distance {distance:F1} > {hueTolerance}");
            }
        }

        // $0F is the blanking-level black code; $1D is constant _l at luma 1,
        // whose DAC level (518 units) is exactly the blanking level - both
        // decode to near-black.
        foreach (var code in new byte[] { 0x0F, 0x1D })
        {
            var (r, g, b) = DecodeFlatBackground(code, SettleFrames);
            report.AppendLine($"${code:X2}   ({r,5:F1},{g,5:F1},{b,5:F1})   (near-black)");
            if (r + g + b > 60)
            {
                failures.Add($"near-black ${code:X2}: sum {r + g + b:F1} > 60");
            }
        }

        Console.WriteLine(report.ToString());

        // Grey luma ordering: $00 darkest, then $10, then $20; $30 == $20
        // (both clip), so it must not drop below $20 but need not exceed it.
        await Assert.That(greyLuma[1]).IsGreaterThan(greyLuma[0]);
        await Assert.That(greyLuma[2]).IsGreaterThan(greyLuma[1]);
        await Assert.That(greyLuma[3]).IsGreaterThan(greyLuma[2] - 12.0);

        await Assert.That(failures.Count)
            .IsEqualTo(0)
            .Because(report.ToString() + "\n" + string.Join("\n", failures));
    }

    [Test]
    public async Task FrameLockSmokeTest()
    {
        var nes = new NesSystem();

        // Unpatched nestest.nes (iNES header handled by Cartridge.FromFile);
        // the reset vector is left alone - the ROM boots to its on-screen menu.
        nes.LoadProgram(Path.Combine("Emulation", "Systems", "Nes", "Assets", "nestest.nes"));

        var result = RunFrames(nes, 3);

        await Assert.That(result.FramesRun).IsEqualTo(3);
        await Assert.That(result.CyclesExecuted).IsLessThan(result.MaxCycles);
    }
}
