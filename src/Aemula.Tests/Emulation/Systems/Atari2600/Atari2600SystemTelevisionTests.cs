using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Aemula.Emulation.Output;
using Aemula.Emulation.Systems.Atari2600;

namespace Aemula.Tests.Emulation.Systems.Atari2600;

// The code-verified replacement for "eyeball the Television window and hope
// it looks right" - loads a small hand-assembled cartridge that paints three
// solid
// background-color bands (COLUBK held at a different value for roughly a
// third of a frame each), runs it, and checks that
// Atari2600System.Television.SampleBuffer (fed live from
// Atari2600System.TickCompositeVideo - see Atari2600System.CompositeVideo.cs)
// actually shows three distinct bands top-to-bottom, the same
// "AssertUniformLitHue"-style live-decode verification
// AppleIISystemTelevisionTests already does for Apple II.
public class Atari2600SystemTelevisionTests
{
    // TIA COLUBK's write-register address (see Atari2600Debugger's own
    // Equates for the full documented list) - the only TIA register this
    // kernel touches, see BuildColorBarCartridge's remarks on why no
    // VSYNC/WSYNC setup is needed either.
    private const byte Colubk = 0x09;

    // Three COLUBK values (hue<<4 | luminance<<1 - real TIA's COLUBK byte
    // layout, confirmed against TiaChip's own COLUBK write handling) with
    // hue codes 5, 10, 15 (roughly 120 degrees apart around TIA's 15-phase
    // color wheel) at a bright, consistent luminance. Chosen empirically,
    // not just by even phase spacing: measured decoded RGB for every one of
    // the 15 hues at this luminance (holding each alone, in isolation, long
    // enough to lock), several other evenly-spaced triples (e.g. hues
    // 2/7/12) decode to a pair that lands under this test's 60-unit RGB
    // distance threshold despite being 120 degrees apart in phase, since the
    // YIQ->RGB matrix doesn't preserve phase separation as RGB distance
    // uniformly around the circle - this triple was checked to clear the
    // threshold in every pairing with margin to spare.
    private const byte ColorABackground = 0x5C; // hue 5,  luma 6
    private const byte ColorBBackground = 0xAC; // hue 10, luma 6
    private const byte ColorCBackground = 0xFC; // hue 15, luma 6

    // Cartridge code starts right at the bottom of the cartridge-selected
    // address window - see BuildColorBarCartridge.
    private const ushort CodeStart = 0x1000;

    // Column range, as a fraction of the buffer width, that every
    // measurement below samples - a strip through the centre of the line.
    // TIA's horizontal blanking is far wider than broadcast RS-170A (68 of
    // 228 color clocks, ~19us, versus ~10.9us), so the real 2600 picture is
    // inset with a black border on both sides - a period TV showed the same
    // border, and Television reproduces it faithfully. Television frames
    // active video by RS-170A proportions, though, so its ActiveVideo region
    // is wider than TIA's actual picture and its edges include
    // blanking-level (byte 64) columns. Averaging those in would drag every
    // luma/chroma reading toward black (~16% low for a full-screen COLUBK);
    // sampling only the centre keeps the measurement on real picture.
    private const double PictureCentreLo = 0.42;
    private const double PictureCentreHi = 0.58;

    private static byte[] BuildColorBarCartridge()
    {
        var code = new List<byte>();

        // No WSYNC/VSYNC scanline sync here, deliberately: TIA's own
        // horizontal/vertical timing (HorizontalCounter/VerticalBlank -
        // see TiaChip.Osc) free-runs entirely off Osc, independent of
        // anything the CPU does, so real picture geometry (which raster row
        // a sample lands on) doesn't depend on the CPU's pace at all. Only
        // *when* COLUBK changes, in real elapsed time, matters for where a
        // band boundary falls - so each band below is held for a fixed,
        // precisely-cycle-counted busy-wait (~6,430 CPU cycles, i.e.
        // ~19,290 OSC ticks) rather than a WSYNC-per-line loop, sidestepping
        // needing WSYNC to actually halt the CPU (Mos6502Chip.Rdy is
        // currently an unwired TODO - see that property's own remarks).
        EmitColorBand(code, ColorABackground);
        EmitColorBand(code, ColorBBackground);
        EmitColorBand(code, ColorCBackground);

        // JMP CodeStart - repeats the three bands forever.
        code.Add(0x4C);
        code.Add((byte)(CodeStart & 0xFF));
        code.Add((byte)(CodeStart >> 8));

        var rom = new byte[4096];
        code.CopyTo(rom);

        // Reset vector ($FFFC/$FFFD, masked down to the 4K image's own
        // $FFC/$FFD by Cartridge4K's 12-bit address mask) -> CodeStart.
        rom[0xFFC] = (byte)(CodeStart & 0xFF);
        rom[0xFFD] = (byte)(CodeStart >> 8);

        return rom;
    }

    // Appends "LDA #colubkValue; STA COLUBK" followed by a fixed-cycle-count
    // busy-wait (an outer/inner DEY/DEX countdown, ~6,430 CPU cycles total)
    // to hold that background color for a good fraction of a frame (a frame
    // is 262 lines * 228 OSC ticks = 59,736 OSC ticks = 19,912 CPU cycles;
    // three of these bands together take ~19,290 CPU cycles, just under one
    // frame, so one pass through all three bands paints (almost) the whole
    // picture).
    private static void EmitColorBand(List<byte> code, byte colubkValue)
    {
        code.Add(0xA9); code.Add(colubkValue);  // LDA #colubkValue
        code.Add(0x85); code.Add(Colubk);       // STA COLUBK

        code.Add(0xA0); code.Add(0x05);         // LDY #5

        var outerStart = code.Count;
        code.Add(0xA2); code.Add(0x00);         // LDX #0

        var innerStart = code.Count;
        code.Add(0xCA);                         // DEX
        EmitBranch(code, 0xD0, innerStart);     // BNE innerStart

        code.Add(0x88);                         // DEY
        EmitBranch(code, 0xD0, outerStart);     // BNE outerStart
    }

    // Backward-branch helper: computes the relative offset from scratch
    // (rather than hand-calculating each one) so the busy-wait loops above
    // can be adjusted without re-deriving branch math by hand.
    private static void EmitBranch(List<byte> code, byte opcode, int targetIndex)
    {
        code.Add(opcode);
        var operandIndex = code.Count;
        code.Add(0);
        var nextInstructionIndex = code.Count;
        code[operandIndex] = unchecked((byte)(targetIndex - nextInstructionIndex));
    }

    private static string WriteCartridgeToTempFile(byte[] rom)
    {
        var path = Path.Combine(Path.GetTempPath(), $"aemula-atari2600-test-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, rom);
        return path;
    }

    // No boot ROM on the Atari 2600 (unlike AppleIISystemTelevisionTests'
    // BootToIdle) - just run enough frames that Television's self-
    // calibrating sync/level tracking, raster oscillators, and color-burst
    // PLL are locked well before the frame under test is captured.
    private static void RunFrames(Atari2600System system, int frameCount)
    {
        // 262 lines * 228 OSC ticks/line - see EmitColorBand's remarks.
        const int ticksPerFrame = 262 * 228;

        for (var i = 0; i < frameCount * ticksPerFrame; i++)
        {
            system.Tick();
        }
    }

    // Scans the picture top-to-bottom one row at a time (only ActiveVideo
    // samples, the same restriction AppleIISystemTelevisionTests'
    // AssertUniformLitHue uses, for the same reason: sync/blanking samples
    // decode to real but meaningless colors - see Television.Decode's own
    // remarks), averaging each row's color, then greedily groups
    // consecutive rows whose average color is still close together into
    // one band. Deliberately doesn't assume *which* row range belongs to
    // which of the three programmed colors, or even how many full bands
    // land in this one captured frame (the three-band cycle is ~97% of a
    // frame - see EmitColorBand's remarks - so a snapshot can catch the
    // cycle at any phase, occasionally splitting one band across the
    // top/bottom edge) - it only checks that the picture really does show
    // several large, mutually-distinct-colored regions, not one uniform
    // color, which is what "color bars rendered" actually means.
    private static List<RgbaByte> FindDistinctRowBands(SampleBuffer buffer)
    {
        var rowAverages = new List<RgbaByte>();

        var columnLo = (int)(buffer.Width * PictureCentreLo);
        var columnHi = (int)(buffer.Width * PictureCentreHi);

        for (var row = 0; row < buffer.Height; row++)
        {
            long sumR = 0, sumG = 0, sumB = 0;
            var count = 0;

            var rowOffset = row * buffer.Width;
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

            // Only count rows that are genuinely part of the picture (the
            // whole centre strip is active video), telling real picture rows
            // apart from blanking ones.
            if (count < (columnHi - columnLo) * 0.5f)
            {
                continue;
            }

            rowAverages.Add(new RgbaByte((byte)(sumR / count), (byte)(sumG / count), (byte)(sumB / count), 255));
        }

        var bands = new List<RgbaByte>();

        // Compared against each band's fixed anchor (its first row's
        // color), not whichever row was seen last - comparing against the
        // last row instead let a chain of small, individually-under-
        // threshold steps drift transitively from one real band's color
        // all the way to another's without ever tripping the threshold in
        // one hop, merging genuinely distinct bands together.
        var bandAnchor = default(RgbaByte);
        long bandSumR = 0, bandSumG = 0, bandSumB = 0;
        var bandRowCount = 0;

        void FinishBand()
        {
            // A real band spans dozens of rows (see EmitColorBand's
            // remarks); a handful of rows is just the transition between
            // two real bands (COLUBK changing mid-scan) averaging out to
            // some in-between color, not a real third color.
            if (bandRowCount >= 15)
            {
                bands.Add(new RgbaByte(
                    (byte)(bandSumR / bandRowCount),
                    (byte)(bandSumG / bandRowCount),
                    (byte)(bandSumB / bandRowCount),
                    255));
            }
        }

        foreach (var rowColor in rowAverages)
        {
            if (bandRowCount == 0 || ColorDistance(rowColor, bandAnchor) < 60)
            {
                if (bandRowCount == 0)
                {
                    bandAnchor = rowColor;
                }

                bandSumR += rowColor.R;
                bandSumG += rowColor.G;
                bandSumB += rowColor.B;
                bandRowCount++;
            }
            else
            {
                FinishBand();
                bandAnchor = rowColor;
                bandSumR = rowColor.R;
                bandSumG = rowColor.G;
                bandSumB = rowColor.B;
                bandRowCount = 1;
            }
        }

        FinishBand();

        return bands;
    }

    private static double ColorDistance(RgbaByte a, RgbaByte b)
    {
        var dr = a.R - b.R;
        var dg = a.G - b.G;
        var db = a.B - b.B;
        return Math.Sqrt(dr * dr + dg * dg + db * db);
    }

    // A cartridge that sets COLUBK once and then spins forever, so the whole
    // picture is one flat color for as long as the test cares to run it.
    private static byte[] BuildSolidBackgroundCartridge(byte colubkValue)
    {
        var rom = new byte[4096];

        // The JMP targets its own address, so the CPU spins on one
        // instruction rather than re-running the STA - COLUBK is written
        // once and then simply left alone, which is what makes the whole
        // frame one flat color.
        const ushort SpinAddress = CodeStart + 4;

        rom[0] = 0xA9; rom[1] = colubkValue; // LDA #colubkValue
        rom[2] = 0x85; rom[3] = Colubk;      // STA COLUBK
        rom[4] = 0x4C;                       // JMP SpinAddress
        rom[5] = SpinAddress & 0xFF;
        rom[6] = SpinAddress >> 8;

        rom[0xFFC] = CodeStart & 0xFF;
        rom[0xFFD] = CodeStart >> 8;

        return rom;
    }

    // Two COLUBK bands alternating down the screen (A, B, A, B - four
    // fixed-cycle holds, together ~1.3 frames, so every visible row lands
    // in one band or the other and at least one full band of each colour is
    // captured). Built from the same EmitColorBand busy-wait
    // BuildColorBarCartridge uses; the point of the pair is a scene whose
    // brightest area is only band A, to check band B decodes identically
    // whether or not A is on screen with it.
    private static byte[] BuildTwoBandCartridge(byte colubkA, byte colubkB)
    {
        var code = new List<byte>();

        EmitColorBand(code, colubkA);
        EmitColorBand(code, colubkB);
        EmitColorBand(code, colubkA);
        EmitColorBand(code, colubkB);

        code.Add(0x4C);
        code.Add((byte)(CodeStart & 0xFF));
        code.Add((byte)(CodeStart >> 8));

        var rom = new byte[4096];
        code.CopyTo(rom);

        rom[0xFFC] = (byte)(CodeStart & 0xFF);
        rom[0xFFD] = (byte)(CodeStart >> 8);

        return rom;
    }

    // Palette.NtscPalette row 0 is achromatic, so each entry's low byte is
    // exactly that luminance code's decoded Y (0, 64, 108, 144, 176, 200,
    // 220, 236 for codes 0..7) - the reference the derived luma curve is
    // shaped from.
    private static double PaletteGreyY(int lum) => Palette.NtscPalette[lum] & 0xFF;

    // The chroma-vector magnitude of one palette entry, taken the same way
    // PaletteHueAngleDegrees takes its angle (standard U/V: 0.492*(b-y),
    // 0.877*(r-y)).
    private static double PaletteChromaMagnitude(int hue, int lum)
    {
        var rgb = Palette.NtscPalette[hue * 8 + lum];
        double r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;

        var y = 0.299 * r + 0.587 * g + 0.114 * b;
        var u = 0.492 * (b - y);
        var v = 0.877 * (r - y);

        return Math.Sqrt(u * u + v * v);
    }

    // Mean decoded Luma over a centre-strip block through the middle of the
    // picture (see PictureCentreLo/Hi) - the luma counterpart of
    // AverageChroma, used where the whole frame is one flat colour.
    private static double AverageLuma(SampleBuffer buffer)
    {
        double sum = 0;
        var count = 0;

        var columnLo = (int)(buffer.Width * PictureCentreLo);
        var columnHi = (int)(buffer.Width * PictureCentreHi);

        for (var row = (int)(buffer.Height * 0.4); row < (int)(buffer.Height * 0.6); row++)
        {
            for (var column = columnLo; column < columnHi; column++)
            {
                var sample = buffer.Data[row * buffer.Width + column];
                if (sample.Region != RasterRegion.ActiveVideo)
                {
                    continue;
                }

                sum += sample.Luma;
                count++;
            }
        }

        return count == 0 ? 0 : sum / count;
    }

    // Per-picture-row mean decoded Luma (over the centre strip, rows that
    // are fully active video there), top to bottom - lets a test pick out
    // one band of a multi-band frame.
    private static List<double> PictureRowLumas(SampleBuffer buffer)
    {
        var rows = new List<double>();

        var columnLo = (int)(buffer.Width * PictureCentreLo);
        var columnHi = (int)(buffer.Width * PictureCentreHi);

        for (var row = 0; row < buffer.Height; row++)
        {
            double sum = 0;
            var count = 0;

            var rowOffset = row * buffer.Width;
            for (var column = columnLo; column < columnHi; column++)
            {
                var sample = buffer.Data[rowOffset + column];
                if (sample.Region != RasterRegion.ActiveVideo)
                {
                    continue;
                }

                sum += sample.Luma;
                count++;
            }

            if (count >= (columnHi - columnLo) * 0.5f)
            {
                rows.Add(sum / count);
            }
        }

        return rows;
    }

    // Where a chroma vector sits on the color wheel, in the standard NTSC
    // (U, V) plane a vectorscope displays - degrees counterclockwise from
    // +U, so burst is at 180, yellow at ~167, red at ~103.
    //
    // NtscYiqDecoder's I and Q are components on axes that sit at 123 and 33
    // degrees in that same plane (its BurstToIAxisRotationRadians remarks
    // derive both figures), and Q's axis is I's rotated -90, which is what
    // collapses the two-axis projection into the single atan2 below.
    private static double DecodedHueAngleDegrees(double i, double q)
        => (123.0 - Math.Atan2(q, i) * 180.0 / Math.PI + 720.0) % 360.0;

    // The same angle for one of Palette.NtscPalette's hue rows, straight
    // from the standard U/V definitions. Averaged as a *phasor* across all
    // eight luminances of the row rather than taken from a single entry:
    // the palette's saturation varies with luminance, its darkest entries
    // clip, and one arbitrarily-chosen luma would make the reference noisier
    // than the thing being measured.
    private static double PaletteHueAngleDegrees(int hue)
    {
        double sumU = 0, sumV = 0;

        for (var luma = 0; luma < 8; luma++)
        {
            var rgb = Palette.NtscPalette[hue * 8 + luma];
            double r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;

            var y = 0.299 * r + 0.587 * g + 0.114 * b;
            sumU += 0.492 * (b - y);
            sumV += 0.877 * (r - y);
        }

        return (Math.Atan2(sumV, sumU) * 180.0 / Math.PI + 360.0) % 360.0;
    }

    // Averages decoded I/Q over a centre-strip block through the middle of
    // the picture (see PictureCentreLo/Hi). I/Q rather than RGB because this
    // test is about *phase*: the YIQ->RGB matrix compresses the hue circle
    // unevenly (the reason ColorBarCartridgeDecodesToDistinctBands below has
    // to pick its three hues empirically rather than just spacing them 120
    // degrees apart), so an RGB-distance check can't express "this hue
    // landed N degrees from where it should have" at all.
    private static (double I, double Q) AverageChroma(SampleBuffer buffer)
    {
        double sumI = 0, sumQ = 0;
        var count = 0;

        var columnLo = (int)(buffer.Width * PictureCentreLo);
        var columnHi = (int)(buffer.Width * PictureCentreHi);

        for (var row = (int)(buffer.Height * 0.4); row < (int)(buffer.Height * 0.6); row++)
        {
            for (var column = columnLo; column < columnHi; column++)
            {
                var sample = buffer.Data[row * buffer.Width + column];
                if (sample.Region != RasterRegion.ActiveVideo)
                {
                    continue;
                }

                sumI += sample.I;
                sumQ += sample.Q;
                count++;
            }
        }

        return count == 0 ? (0, 0) : (sumI / count, sumQ / count);
    }

    // The actual "are the colors right" check, as opposed to
    // ColorBarCartridgeDecodesToDistinctBands' "are the colors different"
    // one: hold each of TIA's 15 hue codes on screen in turn, decode it
    // through the real pipeline, and compare where it lands on the color
    // wheel against Palette.NtscPalette - this codebase's existing
    // hardware-derived reference table.
    //
    // This is the test that pins down *absolute* color phase, and it is
    // worth being clear about why absolute phase is even checkable. Nothing
    // is calibrated per-system anywhere in this path: TIA transmits color
    // burst off the same delay-line tap as hue 1 (TiaChip drives Col = 1
    // during the burst window), NtscColorBurstPll has exactly one stable
    // lock for any signal (see its phase-detector remarks), and
    // NtscYiqDecoder rotates off that recovered burst by the plain
    // spec-derived figure with nothing fitted on top. Burst is precisely
    // what makes absolute hue a fixed point rather than a per-source
    // adjustment - which is why a period television needed no re-tinting
    // when swapping a 2600 for an Apple II, and why this test can assert
    // real hues rather than merely self-consistent ones.
    //
    // Tolerance: 30 degrees, against a measured worst case of ~23 (and a
    // mean of ~-10). That's deliberately loose in absolute terms and still
    // extremely tight against the failures it exists to catch - the
    // burst-rotation bug this replaced sat 167 degrees out on every single
    // hue, and getting HueStepDegrees wrong accumulates ~38 degrees across
    // the sweep. Chasing the residual any lower would be chasing
    // Palette.NtscPalette's own console-specific trim (see
    // Atari2600System.CompositeVideo.cs's HueStepDegrees remarks on the real
    // hardware's color pot), not decoder error, and this project's stated
    // bar is "recognizably correct", not broadcast-accurate colorimetry.
    [Test]
    public async Task EveryHueCodeDecodesCloseToTheReferencePalette()
    {
        const double toleranceDegrees = 30.0;

        for (var hue = 1; hue <= 15; hue++)
        {
            // hue<<4 | luminance<<1 - real TIA's COLUBK byte layout (see
            // ColorABackground above). Luminance 6 for the same reason those
            // constants use it: bright enough to decode cleanly, short of
            // the palette's clipped extremes.
            var system = new Atari2600System();
            var path = WriteCartridgeToTempFile(BuildSolidBackgroundCartridge((byte)((hue << 4) | (6 << 1))));

            try
            {
                system.LoadProgram(path);
                system.Reset();

                RunFrames(system, 20);

                var (i, q) = AverageChroma(system.Television.SampleBuffer);

                // Guards the angle check below from being computed off
                // essentially zero chroma, which would make its phase
                // meaningless and the assertion accidentally vacuous.
                await Assert.That(Math.Sqrt(i * i + q * q)).IsGreaterThan(10.0);

                var decoded = DecodedHueAngleDegrees(i, q);
                var expected = PaletteHueAngleDegrees(hue);

                // Signed, wrapped shortest arc between the two angles.
                var error = Math.Abs(((decoded - expected + 540.0) % 360.0) - 180.0);

                await Assert.That(error).IsLessThan(toleranceDegrees);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    [Test]
    public async Task ColorBarCartridgeDecodesToDistinctBands()
    {
        var system = new Atari2600System();
        var path = WriteCartridgeToTempFile(BuildColorBarCartridge());

        try
        {
            system.LoadProgram(path);
            system.Reset();

            RunFrames(system, 20);

            var bands = FindDistinctRowBands(system.Television.SampleBuffer);

            // The three-band cycle is only ~97% of a frame (see
            // EmitColorBand's remarks), so a snapshot can catch it mid-cycle
            // and split the wrapped-around first band across the top and
            // bottom of the picture, appearing as two same-colored bands in
            // row order rather than one - reduce to each band's distinct
            // color before checking how many *different* colors actually
            // showed up.
            var distinctColors = new List<RgbaByte>();
            foreach (var band in bands)
            {
                if (!distinctColors.Exists(existing => ColorDistance(existing, band) < 60))
                {
                    distinctColors.Add(band);
                }
            }

            await Assert.That(distinctColors.Count).IsGreaterThanOrEqualTo(3);

            for (var i = 0; i < distinctColors.Count; i++)
            {
                for (var j = i + 1; j < distinctColors.Count; j++)
                {
                    await Assert.That(ColorDistance(distinctColors[i], distinctColors[j])).IsGreaterThanOrEqualTo(60);
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    // The grayscale ramp: hold each luminance code 0..7 (hue 0, no chroma)
    // full-screen and check the decoded Luma against Palette.NtscPalette
    // row 0 directly. The emitted curve is that row run backwards through
    // NtscYiqDecoder's normalization (byte = blanking + Y * (WhiteLevel -
    // blanking) / 255), so a settled grey screen decodes straight back to
    // the palette Y - code 7 included, which lands near 236 (~92 IRE), not
    // full white, because real 2600 grey doesn't reach reference white. The
    // monotonic and compressive checks pin the DAC shape the old linear
    // lum/7f curve lacked.
    [Test]
    public async Task GrayscaleRampDecodesToTheCompressivePaletteCurve()
    {
        var lumas = new double[8];

        for (var lum = 0; lum < 8; lum++)
        {
            var system = new Atari2600System();
            var path = WriteCartridgeToTempFile(BuildSolidBackgroundCartridge((byte)(lum << 1)));

            try
            {
                system.LoadProgram(path);
                system.Reset();

                RunFrames(system, 20);

                lumas[lum] = AverageLuma(system.Television.SampleBuffer);
            }
            finally
            {
                File.Delete(path);
            }
        }

        for (var lum = 1; lum < 8; lum++)
        {
            await Assert.That(lumas[lum]).IsGreaterThan(lumas[lum - 1]);
        }

        for (var lum = 0; lum < 8; lum++)
        {
            await Assert.That(Math.Abs(lumas[lum] - PaletteGreyY(lum))).IsLessThan(4.0);
        }

        // Compressive: each decoded step is no larger than the one before it
        // (within a small margin for decode noise), and the top step is well
        // under the bottom one - the property the linear lum/7f curve lacked
        // (its steps were all equal).
        for (var lum = 2; lum < 8; lum++)
        {
            var step = lumas[lum] - lumas[lum - 1];
            var previousStep = lumas[lum - 1] - lumas[lum - 2];
            await Assert.That(step).IsLessThan(previousStep + 6.0);
        }

        await Assert.That(lumas[7] - lumas[6]).IsLessThan((lumas[1] - lumas[0]) * 0.6);
    }

    // Tree-trunk case: (lum 0, hue 1) = COLUBK $10 is a dark olive
    // (Palette.NtscPalette $10 = 0x444400, Y ~= 60), not black. The linear
    // curve collapsed every lum-0 entry onto blanking; the coloured-black
    // floor keeps chroma-bearing entries on their raised sub-range.
    [Test]
    public async Task ColouredLumaZeroIsNotBlack()
    {
        var system = new Atari2600System();
        var path = WriteCartridgeToTempFile(BuildSolidBackgroundCartridge(0x10));

        try
        {
            system.LoadProgram(path);
            system.Reset();

            RunFrames(system, 20);

            var buffer = system.Television.SampleBuffer;
            var luma = AverageLuma(buffer);
            var (i, q) = AverageChroma(buffer);

            await Assert.That(luma).IsGreaterThan(40.0);
            await Assert.That(Math.Sqrt(i * i + q * q)).IsGreaterThan(10.0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Saturation magnitude, complementing EveryHueCodeDecodesCloseToTheReferencePalette's
    // phase-only check: a mid hue (code 12) at lum 3 should decode to a
    // chroma vector whose magnitude is near that palette entry's own
    // |0.492*(b-y), 0.877*(r-y)| (~38). The synthesis stage emits one fixed
    // chroma amplitude for every hue - at the nominal 1.594 decode gain it
    // lands near 41, close for this mid hue - and, being fixed while the
    // palette's saturation varies per hue, it reads a little high for the
    // palette's dimmer hues and low for its most saturated ones.
    [Test]
    public async Task MidHueSaturationMatchesThePalette()
    {
        const int hue = 12;
        const int lum = 3;
        const double toleranceMagnitude = 10.0;

        var system = new Atari2600System();
        var path = WriteCartridgeToTempFile(BuildSolidBackgroundCartridge((byte)((hue << 4) | (lum << 1))));

        try
        {
            system.LoadProgram(path);
            system.Reset();

            RunFrames(system, 20);

            var (i, q) = AverageChroma(system.Television.SampleBuffer);
            var decodedMagnitude = Math.Sqrt(i * i + q * q);

            await Assert.That(Math.Abs(decodedMagnitude - PaletteChromaMagnitude(hue, lum)))
                .IsLessThan(toleranceMagnitude);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // End-to-end gain stability: a dim band (lum 2) must decode to the same
    // Luma whether or not a brighter band (lum 6) shares the screen with it.
    // With the old running-picture-peak AGC the lone-lum-2 frame settled at a
    // lower white reference and inflated gain; the sync-anchored gain makes
    // the two identical. Counterpart to the decoder-level stability check.
    [Test]
    public async Task DimBandDecodesTheSameWithOrWithoutABrightBand()
    {
        const double tolerance = 12.0;

        double soloDimLuma;
        {
            var system = new Atari2600System();
            var path = WriteCartridgeToTempFile(BuildSolidBackgroundCartridge(2 << 1));

            try
            {
                system.LoadProgram(path);
                system.Reset();
                RunFrames(system, 20);
                soloDimLuma = AverageLuma(system.Television.SampleBuffer);
            }
            finally
            {
                File.Delete(path);
            }
        }

        double pairedDimLuma;
        {
            var system = new Atari2600System();
            var path = WriteCartridgeToTempFile(BuildTwoBandCartridge(6 << 1, 2 << 1));

            try
            {
                system.LoadProgram(path);
                system.Reset();
                RunFrames(system, 20);

                var rowLumas = PictureRowLumas(system.Television.SampleBuffer);
                rowLumas.Sort();

                // The dim (lum 2) band's rows are the lower-luma half of the
                // frame; average those, ignoring the few transition rows at
                // each band boundary by trimming the extremes.
                var lower = rowLumas.GetRange(2, rowLumas.Count / 2 - 2);
                pairedDimLuma = 0;
                foreach (var value in lower)
                {
                    pairedDimLuma += value;
                }

                pairedDimLuma /= lower.Count;
            }
            finally
            {
                File.Delete(path);
            }
        }

        await Assert.That(Math.Abs(pairedDimLuma - soloDimLuma)).IsLessThan(tolerance);
    }
}
