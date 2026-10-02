using System;
using System.Threading.Tasks;
using Aemula.Emulation.Output;

namespace Aemula.Tests.Emulation.Output;

// Colour decoding against CompositeTestSignal's independently-encoded bars, for
// both standards: each decoded bar has to come back as the RGB it was encoded
// from, not merely the right order of hues.
public class TelevisionColorTests
{
    private const int Fields = 40;
    private const int Tolerance = 40;

    private static Television Decode(TelevisionStandard standard, int fields = Fields)
    {
        var television = new Television(standard);

        foreach (var sample in CompositeTestSignal.Generate(
            standard, standard.SamplesPerSecond, fields, colorBars: true))
        {
            television.Decode(sample);
        }

        return television;
    }

    private static RgbaByte BarColor(Television television, int bar)
    {
        var buffer = television.SampleBuffer;
        var row = (int)(buffer.Height * 0.7);
        var start = television.ActiveVideoStartSamples;
        var column = (int)(start + (bar + 0.5) * television.ActiveVideoLengthSamples / CompositeTestSignal.Bars.Length);

        return buffer.Data[row * buffer.Width + column].Color;
    }

    private static async Task AssertBarsDecodeToTheirSource(Television television)
    {
        for (var bar = 0; bar < CompositeTestSignal.Bars.Length; bar++)
        {
            var (r, g, b) = CompositeTestSignal.Bars[bar];
            var color = BarColor(television, bar);

            await Assert.That(Math.Abs(color.R - r * 255) < Tolerance)
                .IsTrue().Because($"bar {bar} red: decoded {color.R}, encoded {r * 255:0}");
            await Assert.That(Math.Abs(color.G - g * 255) < Tolerance)
                .IsTrue().Because($"bar {bar} green: decoded {color.G}, encoded {g * 255:0}");
            await Assert.That(Math.Abs(color.B - b * 255) < Tolerance)
                .IsTrue().Because($"bar {bar} blue: decoded {color.B}, encoded {b * 255:0}");
        }
    }

    [Test]
    public async Task PalColorBarsDecodeToTheirEncodedColors()
    {
        var television = Decode(TelevisionStandard.Pal);

        await Assert.That(television.ColorBurstLocked).IsTrue();
        await AssertBarsDecodeToTheirSource(television);
    }

    [Test]
    public async Task NtscColorBarsDecodeToTheirEncodedColors()
    {
        var television = Decode(TelevisionStandard.Ntsc);

        await Assert.That(television.ColorBurstLocked).IsTrue();
        await AssertBarsDecodeToTheirSource(television);
    }

    // Which line of the V-switch cycle the decoder first sees is arbitrary, so
    // it has to find the parity from the burst however the signal starts.
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task PalColorDecodesWhicheverLineTheSignalStartsOn(int leadingLines)
    {
        var standard = TelevisionStandard.Pal;
        var samples = CompositeTestSignal.Generate(standard, standard.SamplesPerSecond, Fields, colorBars: true);
        var television = new Television(standard);
        var skip = (int)Math.Round(leadingLines * television.DetectedSamplesPerLine);

        for (var i = skip; i < samples.Length; i++)
        {
            television.Decode(samples[i]);
        }

        await AssertBarsDecodeToTheirSource(television);
    }
}
