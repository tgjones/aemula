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

    private static void Run(BbcMicroSystem system, int ticks)
    {
        for (var tick = 0; tick < ticks; tick++)
        {
            system.Tick();
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
    private static void Type(BbcMicroSystem system, string text)
    {
        foreach (var character in text)
        {
            var key = new KeyEvent { IsDown = true, Key = (Key)character, Character = character };
            system.OnKeyEvent(key);
            Run(system, 960_000);
            system.OnKeyEvent(new KeyEvent { IsDown = false, Key = (Key)character, Character = character });
            Run(system, 960_000);
        }
    }

    // Runs until the line the cursor is on is the BASIC prompt again, which
    // is how a finished SAVE or LOAD shows.
    private static void RunUntilPrompt(BbcMicroSystem system, int budgetTicks)
    {
        for (var ticks = 0; ticks < budgetTicks; ticks += 1_000_000)
        {
            Run(system, 1_000_000);

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

    private static ushort Crc(IEnumerable<byte> bytes)
    {
        ushort crc = 0;

        foreach (var value in bytes)
        {
            crc ^= (ushort)(value << 8);

            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            }
        }

        return crc;
    }

    // A single-block cassette file holding the one-line BASIC program
    // 10 PRINT "TAPE OK", as a UEF.
    private static byte[] ProgramUef()
    {
        var program = Program();

        var header = new List<byte>();
        header.AddRange("T"u8.ToArray());
        header.Add(0);
        header.AddRange([0x00, 0x19, 0x00, 0x00]);
        header.AddRange([0x23, 0x80, 0x00, 0x00]);
        header.AddRange([0x00, 0x00]);
        header.AddRange(BitConverter.GetBytes((ushort)program.Length));
        header.Add(0x80);
        header.AddRange([0x00, 0x00, 0x00, 0x00]);

        var block = new List<byte> { 0x2A };
        block.AddRange(header);
        var headerCrc = Crc(header);
        block.AddRange([(byte)(headerCrc >> 8), (byte)headerCrc]);
        block.AddRange(program);
        var dataCrc = Crc(program);
        block.AddRange([(byte)(dataCrc >> 8), (byte)dataCrc]);

        var file = new List<byte>(System.Text.Encoding.ASCII.GetBytes("UEF File!\0"));
        file.AddRange([0x0A, 0x00]);

        void Chunk(ushort id, byte[] data)
        {
            file.AddRange(BitConverter.GetBytes(id));
            file.AddRange(BitConverter.GetBytes((uint)data.Length));
            file.AddRange(data);
        }

        Chunk(0x0110, BitConverter.GetBytes((ushort)1_200));
        Chunk(0x0100, block.ToArray());
        Chunk(0x0110, BitConverter.GetBytes((ushort)480));
        return file.ToArray();
    }

    [Test]
    public async Task A_program_on_a_UEF_loads()
    {
        var system = BootToPrompt(out var deck);

        var uef = UefReader.Read(new MemoryStream(ProgramUef()));
        deck.InsertTape(uef.Samples, uef.SampleRate);

        Type(system, "LOAD\"T\"\r");

        deck.Play();
        RunUntilPrompt(system, 60_000_000);

        await Assert.That(ProgramInMemory(system, Program().Length)).IsEquivalentTo(Program());
    }
}
