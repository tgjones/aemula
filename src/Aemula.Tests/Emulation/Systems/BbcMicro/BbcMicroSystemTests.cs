using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Aemula.Emulation.Systems.BbcMicro;

namespace Aemula.Tests.Emulation.Systems.BbcMicro;

public class BbcMicroSystemTests
{
    // The 555's real pulse is 110ms; a few ticks is as long as the CPU needs.
    private const uint ShortResetPulseTicks = 100;

    // Mode 7's screen memory, 25 rows of 40 characters.
    private const int ScreenAddress = 0x7C00;

    private static string ScreenText(BbcMicroSystem system)
    {
        var text = new StringBuilder();
        for (var offset = 0; offset < 40 * 25; offset++)
        {
            var value = system.ReadByteDebug((ushort)(ScreenAddress + offset));
            text.Append(value is >= 32 and < 127 ? (char)value : ' ');
        }

        return text.ToString();
    }

    // Runs until the BASIC prompt has been printed, returning the tick it
    // appeared on.
    private static int BootToPrompt(BbcMicroSystem system)
    {
        for (var tick = 0; tick < 12_000_000; tick++)
        {
            system.Tick();

            if (tick % 100_000 == 0 && ScreenText(system).Contains('>'))
            {
                return tick;
            }
        }

        throw new InvalidOperationException("The BASIC prompt never appeared.");
    }

    [Test]
    public async Task BootsToTheBasicPrompt()
    {
        var system = new BbcMicroSystem(null, ShortResetPulseTicks);

        BootToPrompt(system);

        var screen = ScreenText(system);

        // The OS prints the banner and the language ROM's name, then BASIC
        // prints its prompt; all of it written to the Mode 7 screen by the
        // 6502 running OS 1.20 out of the ROM socket, with BASIC paged in at
        // &8000 by the ROM number latch.
        await Assert.That(screen).Contains("BBC Computer 32K");
        await Assert.That(screen).Contains("BASIC");

        var basic = File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory, "Emulation", "Systems", "BbcMicro", "Roms", "BASIC.rom"));
        await Assert.That(system.ReadByteDebug(0x8000)).IsEqualTo(basic[0]);
        await Assert.That(system.ReadByteDebug(0x9000)).IsEqualTo(basic[0x1000]);
    }

    [Test]
    public async Task BootProgramsTheVideoHardwareThroughThe1MHzBus()
    {
        var system = new BbcMicroSystem(null, ShortResetPulseTicks);

        BootToPrompt(system);

        // The OS has written Mode 7's control register to the video ULA at
        // &FE20, and its register set to the 6845 at &FE00/&FE01, which
        // therefore generates a 312-line frame: 50 vertical syncs a second,
        // 10 in 0.2s.
        await Assert.That(system.VideoUla.Control).IsEqualTo((byte)0x4B);

        var syncs = 0;
        var wasHigh = system.Crtc.VSync;
        for (var tick = 0; tick < 3_200_000; tick++)
        {
            system.Tick();

            var high = system.Crtc.VSync;
            if (high && !wasHigh)
            {
                syncs++;
            }

            wasHigh = high;
        }

        await Assert.That(syncs).IsBetween(9, 11);
    }

    [Test]
    public async Task CpuRunsAt2MHzButOnlyAt1MHzForTheVias()
    {
        var system = new BbcMicroSystem(null, ShortResetPulseTicks);

        var previousRise = -1;
        ushort previousAddress = 0;
        var wasHigh = system.Cpu.Phi2;

        var romAndRamCycles = 0;
        var romAndRamCyclesAt2MHz = 0;
        var viaCycles = 0;
        var viaCyclesAt1MHz = 0;

        // The OS start-up tests RAM from ROM, then initialises both VIAs.
        for (var tick = 0; tick < 8_000_000 && viaCycles < 50; tick++)
        {
            system.Tick();

            var high = system.Cpu.Phi2;
            if (high && !wasHigh)
            {
                if (previousRise >= 0)
                {
                    var length = tick - previousRise;

                    // A cycle's length is measured from its own PHI2 to the
                    // next one.
                    if (previousAddress is >= 0xFE40 and < 0xFE80)
                    {
                        viaCycles++;
                        if (length >= 16)
                        {
                            viaCyclesAt1MHz++;
                        }
                    }
                    else if (previousAddress < 0xFE00)
                    {
                        romAndRamCycles++;
                        if (length == 8)
                        {
                            romAndRamCyclesAt2MHz++;
                        }
                    }
                }

                previousRise = tick;
                previousAddress = system.Cpu.Address;
            }

            wasHigh = high;
        }

        await Assert.That(viaCycles).IsGreaterThan(0);
        await Assert.That(viaCyclesAt1MHz).IsEqualTo(viaCycles);

        // Nearly every other cycle is a 2MHz one; the exceptions are the
        // cycles that are stretched to wait for the 1MHz bus.
        await Assert.That(romAndRamCyclesAt2MHz).IsGreaterThan(romAndRamCycles * 95 / 100);
    }

    [Test]
    public async Task PowerOnResetHoldsTheCpuForTheResetCircuitsPulse()
    {
        var system = new BbcMicroSystem();

        // The reset 555 holds RST# low for 110ms (1.76M ticks): the 6502 is
        // still at the address it powers up with.
        for (var tick = 0; tick < 1_700_000; tick++)
        {
            system.Tick();
        }

        await Assert.That(system.Cpu.Address).IsEqualTo((ushort)0x00FF);

        // Then it runs the reset sequence and starts executing the OS from
        // the vector at &FFFC.
        ushort highestAddress = 0;
        for (var tick = 0; tick < 300_000; tick++)
        {
            system.Tick();
            highestAddress = Math.Max(highestAddress, system.Cpu.Address);
        }

        await Assert.That(highestAddress).IsGreaterThanOrEqualTo((ushort)0xFFFC);
    }
}
