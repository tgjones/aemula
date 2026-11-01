using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Aemula.Emulation.Peripherals;
using Aemula.Emulation.Peripherals.Cassette;
using Aemula.Emulation.Systems.BbcMicro;

namespace Aemula.Tests.Emulation.Systems.BbcMicro;

// The cassette path end to end: BASIC's SAVE drives the ACIA, the serial ULA
// turns its bits into tones and the deck records them; the recording played
// back through the ULA's demodulator and the ACIA is LOADed again.
public class BbcMicroCassetteTests
{
    private const uint ShortResetPulseTicks = 100;
    private const int ScreenAddress = 0x7C00;

    private static void Run(BbcMicroSystem system, int ticks, Action? afterTick = null)
    {
        for (var tick = 0; tick < ticks; tick++)
        {
            system.Tick();
            afterTick?.Invoke();
        }
    }

    private static string ScreenText(BbcMicroSystem system)
    {
        var text = new StringBuilder();

        for (var offset = 0; offset < 40 * 25; offset++)
        {
            var value = system.ReadByteDebug((ushort)(ScreenAddress + offset)) & 0x7F;
            text.Append(value is >= 0x20 and < 0x7F ? (char)value : ' ');
        }

        return text.ToString();
    }

    private static string LastLine(BbcMicroSystem system)
    {
        var text = ScreenText(system);

        for (var row = 24; row >= 0; row--)
        {
            var line = text.Substring(row * 40, 40).TrimEnd();

            if (line.Length > 0)
            {
                return line;
            }
        }

        return "";
    }

    private static BbcMicroSystem BootToPrompt(out CassetteDeck deck)
    {
        var system = new BbcMicroSystem(null, ShortResetPulseTicks);

        var request = system.PeripheralRequests.Single();
        deck = (CassetteDeck)request.Create(new PeripheralContext(system));
        request.Attach(deck);

        for (var tick = 0; tick < 12_000_000; tick++)
        {
            system.Tick();

            if (tick % 100_000 == 0 && ScreenText(system).Contains('>'))
            {
                return system;
            }
        }

        throw new InvalidOperationException("The BASIC prompt never appeared.");
    }

    // Each key is held for three 50Hz frames and left for three, which the
    // OS's keyboard interrupt cannot miss.
    private static void Type(BbcMicroSystem system, string text, Action? afterTick = null)
    {
        foreach (var character in text)
        {
            var key = new KeyEvent { IsDown = true, Key = (Key)character, Character = character };
            system.OnKeyEvent(key);
            Run(system, 960_000, afterTick);
            system.OnKeyEvent(new KeyEvent { IsDown = false, Key = (Key)character, Character = character });
            Run(system, 960_000, afterTick);
        }
    }

    // Runs until the line the cursor is on is the BASIC prompt again, which
    // is how a finished SAVE or LOAD shows.
    private static void RunUntilPrompt(BbcMicroSystem system, int budgetTicks, Action? afterTick = null)
    {
        for (var ticks = 0; ticks < budgetTicks; ticks += 1_000_000)
        {
            Run(system, 1_000_000, afterTick);

            if (LastLine(system) == ">")
            {
                return;
            }
        }

        throw new InvalidOperationException($"The prompt never came back; the screen reads:\n{ScreenText(system)}");
    }

    // With no disc filing system the program area (PAGE) starts at &0E00.
    private const int Page = 0x0E00;

    // The tokenised BASIC program 10 PRINT "TAPE OK".
    private static byte[] Program()
    {
        var line = new List<byte> { 0xF1, 0x20 };
        line.AddRange(System.Text.Encoding.ASCII.GetBytes("\"TAPE OK\""));

        var program = new List<byte> { 0x0D, 0x00, 0x0A, (byte)(line.Count + 4) };
        program.AddRange(line);
        program.AddRange([0x0D, 0xFF]);
        return program.ToArray();
    }

    // Puts the program straight into memory the way BASIC would hold it, with
    // TOP (&12) just past its end.
    private static void PokeProgram(BbcMicroSystem system, byte[] program)
    {
        for (var index = 0; index < program.Length; index++)
        {
            system.WriteByteDebug((ushort)(Page + index), program[index]);
        }

        system.WriteByteDebug(0x12, (byte)((Page + program.Length) & 0xFF));
        system.WriteByteDebug(0x13, (byte)((Page + program.Length) >> 8));
    }

    private static byte[] ProgramInMemory(BbcMicroSystem system, int length) =>
        Enumerable.Range(0, length).Select(index => system.ReadByteDebug((ushort)(Page + index))).ToArray();

    [Test]
    public async Task A_saved_program_loads_back_from_the_recording()
    {
        var system = BootToPrompt(out var deck);
        var program = Program();
        PokeProgram(system, program);

        Type(system, "SAVE\"T\"\r");

        deck.StartRecording();
        Type(system, "\r");

        await Assert.That(system.CassetteMotor).IsTrue();

        RunUntilPrompt(system, 220_000_000);
        deck.StopRecording();

        await Assert.That(system.CassetteMotor).IsFalse();

        // The OS's leader tone is several seconds; a few tenths of a second
        // of it is all the loader needs.
        var recording = deck.RecordedSamples.ToArray();
        await Assert.That(recording.Length).IsGreaterThan(300_000);
        var trimmed = recording.Take(24_000).Concat(recording.Skip(216_000)).ToArray();

        for (var index = 0; index < program.Length; index++)
        {
            system.WriteByteDebug((ushort)(Page + index), 0);
        }

        Type(system, "LOAD\"T\"\r");

        deck.InsertTape(trimmed, CassetteRecorder.SampleRate);
        deck.Play();
        RunUntilPrompt(system, 100_000_000);

        await Assert.That(ProgramInMemory(system, program.Length)).IsEquivalentTo(program);
    }

    private static string AssetPath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Emulation", "Systems", "BbcMicro", "Assets", name);

    // A single-block cassette file holding the one-line BASIC program
    // 10 PRINT "TAPE OK", written by tools/jsbeeb-harness/tape-load.mjs.
    private static (float[] Samples, int SampleRate) ProgramUef() => UefReader.Read(File.OpenRead(AssetPath("tape-program.uef")));

    [Test]
    public async Task A_program_on_a_UEF_loads()
    {
        var system = BootToPrompt(out var deck);

        var uef = ProgramUef();
        deck.InsertTape(uef.Samples, uef.SampleRate);

        Type(system, "LOAD\"T\"\r");

        deck.Play();
        RunUntilPrompt(system, 60_000_000);

        await Assert.That(ProgramInMemory(system, Program().Length)).IsEquivalentTo(Program());
    }

    [Test]
    public async Task A_program_on_a_WAV_loads()
    {
        var system = BootToPrompt(out var deck);
        var program = Program();
        PokeProgram(system, program);

        Type(system, "SAVE\"T\"\r");
        deck.StartRecording();
        Type(system, "\r");
        RunUntilPrompt(system, 220_000_000);

        var wavPath = Path.Combine(Path.GetTempPath(), $"bbc-tape-{Guid.NewGuid():N}.wav");

        try
        {
            deck.SaveRecording(wavPath);

            for (var index = 0; index < program.Length; index++)
            {
                system.WriteByteDebug((ushort)(Page + index), 0);
            }

            Type(system, "LOAD\"T\"\r");

            deck.InsertTape(wavPath);
            deck.Play();
            RunUntilPrompt(system, 220_000_000);
        }
        finally
        {
            File.Delete(wavPath);
        }

        await Assert.That(ProgramInMemory(system, program.Length)).IsEquivalentTo(program);
    }

    // tools/jsbeeb-harness/tape-load.mjs loads the same UEF in jsbeeb and
    // records the addresses first fetched during the LOAD, in order, that
    // neither the boot nor *TAPE had already run. The tape's bits reach the
    // ACIA at times that depend on where each emulator's tape clock starts,
    // so polling loops repeat a different number of times and the traces
    // cannot be compared fetch for fetch; the code that runs and the order it
    // is first reached in must agree.
    [Test]
    public async Task Loading_a_UEF_runs_the_same_code_in_the_same_order_as_jsbeeb()
    {
        var wantedPcs = new List<ushort>();
        var wantedProgram = "";

        foreach (var line in File.ReadAllLines(AssetPath("jsbeeb-tape-load.txt")))
        {
            var parts = line.Split(' ', 2);

            if (parts[0] == "p")
            {
                wantedPcs.Add(Convert.ToUInt16(parts[1], 16));
            }
            else if (parts[0] == "ram")
            {
                wantedProgram = parts[1];
            }
        }

        var system = BootToPrompt(out var deck);

        var seen = new HashSet<ushort>();
        var fresh = new List<ushort>();
        var recording = false;
        var wasHigh = system.Cpu.Phi2;

        void Observe()
        {
            var high = system.Cpu.Phi2;

            if (high && !wasHigh && system.Cpu.Sync && seen.Add(system.Cpu.Address) && recording)
            {
                fresh.Add(system.Cpu.Address);
            }

            wasHigh = high;
        }

        // The fixture leaves out what *TAPE ran; run it under the observer so
        // the same addresses count as already known.
        Type(system, "*TAPE\r", Observe);
        RunUntilPrompt(system, 20_000_000, Observe);

        var uef = ProgramUef();
        deck.InsertTape(uef.Samples, uef.SampleRate);
        deck.Play();
        recording = true;
        Type(system, "LOAD\"T\"\r", Observe);
        RunUntilPrompt(system, 60_000_000, Observe);

        await Assert.That(Convert.ToHexString(ProgramInMemory(system, wantedProgram.Length / 2)).ToLowerInvariant())
            .IsEqualTo(wantedProgram);

        // Code the OS's boot ran in one emulator's idle loop but not the
        // other's (the keyboard scan, say) can show up on one side only; keep
        // the addresses both reached.
        var common = fresh.Where(wantedPcs.Contains).ToList();
        var expected = wantedPcs.Where(common.Contains).ToList();

        await Assert.That(common.Count).IsGreaterThan(wantedPcs.Count * 9 / 10);

        var firstDifference = Enumerable.Range(0, Math.Min(common.Count, expected.Count))
            .FirstOrDefault(i => common[i] != expected[i], -1);
        await Assert.That(firstDifference).IsEqualTo(-1);
    }
}
