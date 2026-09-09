using System.IO;
using System.Threading.Tasks;
using Aemula.Emulation.Output.Wav;
using Aemula.Emulation.Peripherals.Cassette;

namespace Aemula.Tests.Emulation.Systems.AppleI;

public class CassetteRecorderTests
{
    [Test]
    public async Task CapturesNothingUntilStarted()
    {
        var recorder = new CassetteRecorder(phi2Rate: 1_000_000);
        recorder.Level = true;

        for (var i = 0; i < 1000; i++)
        {
            recorder.Tick();
        }

        await Assert.That(recorder.Samples.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SamplesTheLevelAtItsOutputRate()
    {
        // 1 MHz φ2 for 1 M ticks is one second, so ~48 000 output samples.
        var recorder = new CassetteRecorder(phi2Rate: 1_000_000);
        recorder.Start();
        recorder.Level = true;

        for (var i = 0; i < 1_000_000; i++)
        {
            recorder.Tick();
        }

        await Assert.That(recorder.Samples.Count).IsBetween(47_900, 48_100);
        await Assert.That(recorder.Samples[0]).IsEqualTo(1f);
    }

    [Test]
    public async Task RecordsALevelSquareWaveThatSurvivesAWavRoundTrip()
    {
        var recorder = new CassetteRecorder(phi2Rate: 960_000); // 20 φ2 ticks per output sample
        recorder.Start();

        // 50 output samples high, then 50 low.
        var level = true;
        for (var block = 0; block < 2; block++)
        {
            recorder.Level = level;
            for (var i = 0; i < 50 * 20; i++)
            {
                recorder.Tick();
            }

            level = !level;
        }

        recorder.Stop();

        using var stream = new MemoryStream();
        recorder.WriteWav(stream);
        stream.Position = 0;
        var decoded = WavReader.Read(stream);

        await Assert.That(decoded.SampleRate).IsEqualTo(CassetteRecorder.SampleRate);
        await Assert.That(decoded.Samples[10]).IsBetween(0.999f, 1.0f);
        await Assert.That(decoded.Samples[80]).IsBetween(-1.0f, -0.999f);
    }
}
