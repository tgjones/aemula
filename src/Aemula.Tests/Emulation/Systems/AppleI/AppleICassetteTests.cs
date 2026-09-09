using System.Linq;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Aemula.Emulation.Output.Wav;
using Aemula.Emulation.Peripherals.Cassette;
using Aemula.Emulation.Systems;
using Aemula.Emulation.Systems.AppleI;
using Hexa.NET.SDL3;

namespace Aemula.Tests.Emulation.Systems.AppleI;

// Drives the real WozMon + ACI firmware end to end: type a WRITE command,
// record what the tape-out flip-flop produces, save it as a WAV, play that WAV
// back into the tape-in comparator, type a READ command into a different
// address range, and check the bytes came back. Nothing here reaches past the
// two audio jacks and the keyboard - the FSK encode and decode are entirely
// the ROM's, and the ~10-second leader tone the ACI always writes makes this a
// slow test (a few seconds in Release, much longer in Debug).
public class AppleICassetteTests
{
    private const int MasterTicksPerFrame = 262 * 65 * 14;
    private const ushort NextCharLoop = 0xFF29;
    private const ushort AciEntry = 0xC100;
    private const ushort WozMonEscape = 0xFF1A;

    private static void TypeKey(AppleISystem system, char character) =>
        system.OnKeyEvent(new SDLKeyboardEvent
        {
            Type = SDLEventType.KeyDown,
            Key = character,
            Scancode = SDLScancode.Unknown,
        });

    // Paced the way the console harness types: one key, then a couple of
    // frames for the firmware's keyboard poll to consume and echo it.
    private static void Type(AppleISystem system, string text)
    {
        foreach (var character in text)
        {
            TypeKey(system, character);
            RunFrames(system, 2);
        }
    }

    private static void RunFrames(AppleISystem system, int frames)
    {
        for (var i = 0; i < MasterTicksPerFrame * frames; i++)
        {
            system.Tick();
        }
    }

    private static bool RunUntilFetch(AppleISystem system, ushort address, int frameBudget)
    {
        // long: BASIC's tape needs a ~20k-frame budget, and 262*65*14 * 20000
        // overflows int.
        for (long i = 0; i < (long)MasterTicksPerFrame * frameBudget; i++)
        {
            system.Tick();

            if (system.Cpu.Sync && system.Cpu.Address == address)
            {
                return true;
            }
        }

        return false;
    }

    // The Apple I as the catalog assembles it: ACI card fitted, a cassette deck
    // patched to its jacks, $E000 RAM present.
    private static (AppleISystem System, CassetteDeck Deck) NewAppleIWithCassette()
    {
        var rig = EmulatedSystems.FindById("applei")!.Build();
        return ((AppleISystem)rig.System, rig.GetPeripheral<CassetteDeck>()!);
    }

    [Test]
    public async Task WriteThenReadRoundTripsAMemoryBlockThroughTheTape()
    {
        var (system, deck) = NewAppleIWithCassette();
        system.LoadProgram("");
        await Assert.That(RunUntilFetch(system, NextCharLoop, frameBudget: 8)).IsTrue();

        // A distinctive pattern the READ can't reproduce by accident; the
        // destination range starts cleared so a no-op read would fail.
        var pattern = new byte[16];
        for (var i = 0; i < pattern.Length; i++)
        {
            pattern[i] = (byte)(0xA5 ^ (i * 7));
            system.WriteByteDebug((ushort)(0x0300 + i), pattern[i]);
            system.WriteByteDebug((ushort)(0x0400 + i), 0x00);
        }

        // Into the ACI, then WRITE $0300-$030F to tape while recording.
        Type(system, "C100R\r");
        await Assert.That(RunUntilFetch(system, AciEntry, frameBudget: 60)).IsTrue();

        deck.StartRecording();
        Type(system, "300.30FW\r");
        await Assert.That(RunUntilFetch(system, WozMonEscape, frameBudget: 1500)).IsTrue();

        // Save the recording as a WAV and play it straight back in.
        using var wavStream = new MemoryStream();
        deck.SaveRecording(wavStream);
        wavStream.Position = 0;
        var tape = WavReader.Read(wavStream);
        await Assert.That(tape.Samples.Length).IsGreaterThan(100_000);

        // Back into the ACI; mount the tape and press PLAY (it goes in
        // stopped), then read into a different range.
        Type(system, "C100R\r");
        await Assert.That(RunUntilFetch(system, AciEntry, frameBudget: 60)).IsTrue();
        deck.InsertTape(tape.Samples, tape.SampleRate);
        deck.Play();

        Type(system, "400.40FR\r");
        await Assert.That(RunUntilFetch(system, WozMonEscape, frameBudget: 2000)).IsTrue();

        for (var i = 0; i < pattern.Length; i++)
        {
            await Assert.That(system.ReadByteDebug((ushort)(0x0400 + i))).IsEqualTo(pattern[i]);
        }
    }

    // A real-world tape: Apple 1 Integer BASIC, from the Internet Archive.
    // Skipped by default - it's a 3 MB download and BASIC's tape is minutes of
    // emulated decoding - but it's the check that the ACI reads a genuine
    // period-recorded tape, not just one this emulator wrote. Un-skip to run.
    [Test]
    [Skip("Downloads a 3 MB WAV and decodes ~4 KB of real tape; run on demand.")]
    public async Task LoadsIntegerBasicFromARealArchiveTape()
    {
        var cachePath = Path.Combine(Path.GetTempPath(), "aemula-apple1-basic.wav");
        if (!File.Exists(cachePath))
        {
            using var http = new HttpClient();
            var bytes = await http.GetByteArrayAsync("https://archive.org/download/Apple1Cassettes/BASIC.wav");
            await File.WriteAllBytesAsync(cachePath, bytes);
        }

        // BASIC runs from the $E000 RAM expansion, which the assembled machine has.
        var (system, deck) = NewAppleIWithCassette();
        system.LoadProgram("");
        await Assert.That(RunUntilFetch(system, NextCharLoop, frameBudget: 8)).IsTrue();

        // The operator's workflow: mount the tape (it goes in stopped), press
        // PLAY, dawdle a second, then type the load command. The recorded
        // leader tone covers the gap.
        deck.InsertTape(cachePath);
        deck.Play();
        RunFrames(system, 90);

        Type(system, "C100R\r");
        await Assert.That(RunUntilFetch(system, AciEntry, frameBudget: 60)).IsTrue();

        // Integer BASIC loads at $E000-$EFFF.
        Type(system, "E000.EFFFR\r");
        await Assert.That(RunUntilFetch(system, WozMonEscape, frameBudget: 20_000)).IsTrue();

        var image = Enumerable.Range(0xE000, 0x1000)
            .Select(a => system.ReadByteDebug((ushort)a))
            .ToArray();

        // A real 4 KB program landed: not left blank, not left as open bus.
        await Assert.That(image.Any(b => b != 0x00)).IsTrue();
        await Assert.That(image.Any(b => b != 0xFF)).IsTrue();
        await Assert.That(image[0]).IsEqualTo((byte)0x4C); // JMP - BASIC's cold-start entry
    }
}
