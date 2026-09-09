using System;
using System.Threading.Tasks;
using Aemula.Emulation.Peripherals.Cassette;

namespace Aemula.Tests.Emulation.Systems.AppleI;

public class CassetteDeckTests
{
    private const double Phi2Rate = 1_000_000.0;

    // A ~2 kHz square wave: +/-0.8 flipping every quarter-millisecond, long
    // enough to leave a few hundred output samples once resampled to 48 kHz.
    private static float[] SquareWaveTape(int halfPeriods)
    {
        const int half = 250; // φ2 cycles per half period at Phi2Rate
        var tape = new float[half * halfPeriods];
        for (var i = 0; i < tape.Length; i++)
        {
            tape[i] = (i / half) % 2 == 0 ? 0.8f : -0.8f;
        }

        return tape;
    }

    // Pump `cycles` interface cycles, driving both leads exactly as the ACI card
    // does: sample the playback lead, then push the tape-out level.
    private static void RunCycles(CassetteDeck deck, int cycles, float tapeOut = 0f)
    {
        for (var i = 0; i < cycles; i++)
        {
            deck.ReadPlayback();
            deck.WriteCapture(tapeOut);
        }
    }

    private static float[] DrainAudio(CassetteDeck deck)
    {
        var source = deck.Audio!;
        var chunk = new float[1024];
        var all = new System.Collections.Generic.List<float>();
        int produced;
        while ((produced = source.Read(chunk)) > 0)
        {
            all.AddRange(new ReadOnlySpan<float>(chunk, 0, produced).ToArray());
        }

        return all.ToArray();
    }

    private static double Rms(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0)
        {
            return 0.0;
        }

        var sum = 0.0;
        foreach (var s in samples)
        {
            sum += (double)s * s;
        }

        return Math.Sqrt(sum / samples.Length);
    }

    [Test]
    public async Task ModeRunsTheReelAndTheRecorderToMatch()
    {
        var deck = new CassetteDeck(Phi2Rate);

        deck.Mode = CassetteDeck.TransportMode.Playing;
        await Assert.That(deck.IsRunning).IsTrue();
        await Assert.That(deck.IsRecording).IsFalse();

        deck.Mode = CassetteDeck.TransportMode.Recording;
        await Assert.That(deck.IsRunning).IsFalse();
        await Assert.That(deck.IsRecording).IsTrue();

        deck.Mode = CassetteDeck.TransportMode.Stopped;
        await Assert.That(deck.IsRunning).IsFalse();
        await Assert.That(deck.IsRecording).IsFalse();
    }

    [Test]
    public async Task PressingRecordBeginsAFreshRecording()
    {
        var deck = new CassetteDeck(Phi2Rate);

        deck.StartRecording();
        RunCycles(deck, 5_000, tapeOut: 1f);
        deck.StopRecording();
        await Assert.That(deck.RecordedSamples.Count).IsGreaterThan(0);

        // Pressing RECORD again discards the previous take rather than appending.
        deck.StartRecording();
        await Assert.That(deck.RecordedSamples.Count).IsEqualTo(0);
    }

    [Test]
    public async Task InsertingATapeForcesStopEvenWhilePlaying()
    {
        var deck = new CassetteDeck(Phi2Rate);
        deck.Play();

        deck.InsertTape(SquareWaveTape(halfPeriods: 4), (int)Phi2Rate);

        await Assert.That(deck.Mode).IsEqualTo(CassetteDeck.TransportMode.Stopped);
        await Assert.That(deck.IsRunning).IsFalse();
    }

    [Test]
    public async Task MonitorCarriesThePlaybackLeadWhilePlayingThenFallsSilentOnStop()
    {
        var deck = new CassetteDeck(Phi2Rate);
        var tape = SquareWaveTape(halfPeriods: 80); // 20 ms of tone
        deck.InsertTape(tape, (int)Phi2Rate);

        deck.Play();
        RunCycles(deck, tape.Length);
        var whilePlaying = DrainAudio(deck);

        deck.Stop();
        RunCycles(deck, 40_000); // ~40 ms, well past the monitor's DC-blocker tail
        var afterStop = DrainAudio(deck);

        await Assert.That(Rms(whilePlaying)).IsGreaterThan(0.2);
        await Assert.That(Rms(afterStop[^200..])).IsLessThan(0.02);
    }

    [Test]
    public async Task MonitorCarriesTheTapeOutLeadWhileRecordingAndIsMutedWhenStopped()
    {
        var deck = new CassetteDeck(Phi2Rate);

        // Record with the interface's tape-out line toggling every 250 cycles
        // (~2 kHz), the way its poll loop would drive it.
        deck.StartRecording();
        for (var block = 0; block < 80; block++)
        {
            RunCycles(deck, 250, tapeOut: block % 2 == 0 ? 1f : 0f);
        }

        var whileRecording = DrainAudio(deck);

        // Stopped, the same toggling tape-out reaches neither the buffer nor the
        // monitor.
        deck.Stop();
        for (var block = 0; block < 160; block++)
        {
            RunCycles(deck, 250, tapeOut: block % 2 == 0 ? 1f : 0f);
        }

        var afterStop = DrainAudio(deck);

        await Assert.That(Rms(whileRecording)).IsGreaterThan(0.2);
        await Assert.That(Rms(afterStop[^200..])).IsLessThan(0.02);
    }
}
