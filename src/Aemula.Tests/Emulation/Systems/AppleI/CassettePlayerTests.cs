using System.Threading.Tasks;
using Aemula.Emulation.Peripherals.Cassette;

namespace Aemula.Tests.Emulation.Systems.AppleI;

public class CassettePlayerTests
{
    [Test]
    public async Task ReturnsSilenceWithNoTape()
    {
        var player = new CassettePlayer(consumerSampleRate: 1000);

        await Assert.That(player.IsPlaying).IsEqualTo(false);
        await Assert.That(player.NextSample()).IsEqualTo(0f);
    }

    [Test]
    public async Task UpsamplingInterpolatesBetweenSourceSamples()
    {
        // Source: 0, 1 at 1 kHz. Consumer at 4 kHz -> one source step per four
        // calls, so the first few outputs walk 0.00, 0.25, 0.50, 0.75, 1.00.
        var player = new CassettePlayer(consumerSampleRate: 4000);
        player.Insert([0f, 1f], sourceSampleRate: 1000);

        await Assert.That(player.NextSample()).IsBetween(-0.001f, 0.001f);
        await Assert.That(player.NextSample()).IsBetween(0.249f, 0.251f);
        await Assert.That(player.NextSample()).IsBetween(0.499f, 0.501f);
        await Assert.That(player.NextSample()).IsBetween(0.749f, 0.751f);
        await Assert.That(player.NextSample()).IsBetween(0.999f, 1.001f);
    }

    [Test]
    public async Task PlaysOutThenReturnsSilence()
    {
        var player = new CassettePlayer(consumerSampleRate: 1000);
        player.Insert([1f, 1f, 1f], sourceSampleRate: 1000);

        for (var i = 0; i < 3; i++)
        {
            await Assert.That(player.NextSample()).IsEqualTo(1f);
        }

        await Assert.That(player.IsPlaying).IsEqualTo(false);
        await Assert.That(player.NextSample()).IsEqualTo(0f);
    }

    [Test]
    public async Task StoppedReelHoldsPositionAndLevel()
    {
        var player = new CassettePlayer(consumerSampleRate: 1000);
        player.Insert([0.2f, 0.4f, 0.6f, 0.8f], sourceSampleRate: 1000);

        player.NextSample(); // 0.2, cursor -> 1
        player.IsRunning = false;

        // Held at the current sample, no advance, for as many reads as you like.
        for (var i = 0; i < 10; i++)
        {
            await Assert.That(player.NextSample()).IsBetween(0.399f, 0.401f);
        }

        // Resuming picks up from exactly where it stopped: the held sample once
        // more, then it advances again.
        player.IsRunning = true;
        await Assert.That(player.NextSample()).IsBetween(0.399f, 0.401f);
        await Assert.That(player.NextSample()).IsBetween(0.599f, 0.601f);
    }

    [Test]
    public async Task ReportsPositionAndLength()
    {
        var player = new CassettePlayer(consumerSampleRate: 1000);

        await Assert.That(player.HasTape).IsEqualTo(false);
        await Assert.That(player.LengthSeconds).IsEqualTo(0.0);

        // 2000 source samples at 500 Hz -> 4 seconds of tape.
        player.Insert(new float[2000], sourceSampleRate: 500);

        await Assert.That(player.HasTape).IsEqualTo(true);
        await Assert.That(player.LengthSeconds).IsEqualTo(4.0);
        await Assert.That(player.PositionSeconds).IsEqualTo(0.0);

        for (var i = 0; i < 1000; i++) // 1000 consumer cycles = 1 second
        {
            player.NextSample();
        }

        await Assert.That(player.PositionSeconds).IsBetween(0.99, 1.01);
    }

    [Test]
    public async Task RewindReplaysFromTheStart()
    {
        var player = new CassettePlayer(consumerSampleRate: 1000);
        player.Insert([0.5f, -0.5f], sourceSampleRate: 1000);

        player.NextSample();
        player.NextSample();
        player.Rewind();

        await Assert.That(player.NextSample()).IsBetween(0.499f, 0.501f);
    }

    [Test]
    public async Task EjectSilencesThePlayer()
    {
        var player = new CassettePlayer(consumerSampleRate: 1000);
        player.Insert([1f, 1f], sourceSampleRate: 1000);

        player.Eject();

        await Assert.That(player.IsPlaying).IsEqualTo(false);
        await Assert.That(player.NextSample()).IsEqualTo(0f);
    }

    [Test]
    public async Task PreservesZeroCrossingTimingWhenUpsampling()
    {
        // A 2 kHz square-ish ramp wave sampled at 40 kHz, replayed at ~1 MHz.
        // The interpolated stream must cross zero at the same times (within one
        // source sample) as the original.
        const int sourceRate = 40_000;
        var source = new float[sourceRate / 100];
        for (var i = 0; i < source.Length; i++)
        {
            source[i] = System.MathF.Sin(i * 2f * System.MathF.PI * 2000f / sourceRate);
        }

        var player = new CassettePlayer(consumerSampleRate: 1_000_000);
        player.Insert(source, sourceRate);

        var lastSign = 0;
        var crossings = 0;
        var samples = (int)((source.Length / (double)sourceRate) * 1_000_000);
        for (var i = 0; i < samples; i++)
        {
            var value = player.NextSample();
            var sign = value > 0f ? 1 : (value < 0f ? -1 : 0);
            if (sign != 0 && lastSign != 0 && sign != lastSign)
            {
                crossings++;
            }

            if (sign != 0)
            {
                lastSign = sign;
            }
        }

        // 2 kHz over 10 ms -> 20 full cycles -> ~40 zero crossings.
        await Assert.That(crossings).IsBetween(38, 42);
    }
}
