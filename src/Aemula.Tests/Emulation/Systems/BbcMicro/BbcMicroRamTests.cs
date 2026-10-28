using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Aemula.Emulation.Systems.BbcMicro;

namespace Aemula.Tests.Emulation.Systems.BbcMicro;

// The RAM is shared with the video circuits through the DRAM cycle logic of the board (IC8-IC13,
// IC43-IC45): the CPU gets one 4MHz slot of every 2MHz cycle and the video ULA the other, so a RAM
// access never makes the CPU wait, in any mode, and the video ULA sees the byte at the address the 6845
// is putting out.
public class BbcMicroRamTests
{
    private const uint ShortResetPulseTicks = 100;
    private const ushort Program = 0x2000;
    private const ushort Loop = 0x201E;
    private const ushort Parameters = 0x2F00;

    // Runs from &2000 once the OS's interrupt handler is pointed at it (IRQ1V): sets the video ULA's
    // control register and the 6845's start address from three parameter bytes, then loops, 256 times
    // round: INC &3000, LDA &3000, STA &3100,X, INX, BNE (6+4+5+2+3 = 20 cycles).
    private static readonly byte[] ProgramBytes =
    [
        0xAD, 0x00, 0x2F, 0x8D, 0x20, 0xFE,
        0xA9, 0x0C, 0x8D, 0x00, 0xFE, 0xAD, 0x01, 0x2F, 0x8D, 0x01, 0xFE,
        0xA9, 0x0D, 0x8D, 0x00, 0xFE, 0xAD, 0x02, 0x2F, 0x8D, 0x01, 0xFE,
        0xA2, 0x00,
        0xEE, 0x00, 0x30, 0xAD, 0x00, 0x30, 0x9D, 0x00, 0x31, 0xE8, 0xD0, 0xF4,
        0x4C, 0x00, 0x20,
    ];

    // The video ULA control register and the 6845's start address (R12, R13) for the BBC's modes.
    public static IEnumerable<(string Name, byte Control, byte StartHigh, byte StartLow)> Modes()
    {
        yield return ("Mode 0", 0x9C, 0x06, 0x00);
        yield return ("Mode 1", 0xD8, 0x0C, 0x00);
        yield return ("Mode 2", 0xF4, 0x0C, 0x00);
        yield return ("Mode 4", 0x88, 0x0C, 0x00);
        yield return ("Mode 5", 0xC4, 0x0C, 0x00);
        yield return ("Mode 7", 0x4B, 0x28, 0x00);
    }

    private static BbcMicroSystem BootToPrompt()
    {
        var system = new BbcMicroSystem(null, ShortResetPulseTicks);

        for (var tick = 0; tick < 12_000_000; tick++)
        {
            system.Tick();

            if (tick % 100_000 == 0 && ScreenHasPrompt(system))
            {
                return system;
            }
        }

        throw new InvalidOperationException("The BASIC prompt never appeared.");
    }

    private static bool ScreenHasPrompt(BbcMicroSystem system)
    {
        for (var offset = 0; offset < 40 * 25; offset++)
        {
            if (system.ReadByteDebug((ushort)(0x7C00 + offset)) == (byte)'>')
            {
                return true;
            }
        }

        return false;
    }

    [Test]
    public async Task ARamLoopTakesItsNominalCyclesInEveryMode()
    {
        var system = BootToPrompt();

        for (var i = 0; i < ProgramBytes.Length; i++)
        {
            system.WriteByteDebug((ushort)(Program + i), ProgramBytes[i]);
        }

        // IRQ1V: the next 100Hz interrupt enters the program.
        system.WriteByteDebug(0x0204, Program & 0xFF);
        system.WriteByteDebug(0x0205, Program >> 8);

        var wasHigh = system.Cpu.Phi2;
        var previousMa = system.Crtc.MA;
        var previousRa = system.Crtc.RA;
        var entered = false;
        var modes = new List<(string Name, byte Control, byte StartHigh, byte StartLow)>(Modes());
        var mode = 0;

        void Select(int index)
        {
            system.WriteByteDebug(Parameters, modes[index].Control);
            system.WriteByteDebug(Parameters + 1, modes[index].StartHigh);
            system.WriteByteDebug(Parameters + 2, modes[index].StartLow);
        }

        Select(0);

        long lastLoopTick = -1;
        var iterations = 0;
        var wrongIntervals = 0;
        var videoFetches = 0;
        var wrongVideoBytes = 0;
        var changedBytes = 0;

        for (long tick = 0; tick < 6_000_000 && mode < modes.Count; tick++)
        {
            system.Tick();

            var high = system.Cpu.Phi2;
            if (high && !wasHigh && system.Cpu.Sync)
            {
                var pc = system.Cpu.Address;

                if (pc == Program)
                {
                    entered = true;
                    lastLoopTick = -1;
                }
                else if (pc == Loop && entered)
                {
                    if (lastLoopTick >= 0)
                    {
                        // 20 cycles of the 2MHz CPU clock are 160 ticks.
                        if (tick - lastLoopTick != 160)
                        {
                            wrongIntervals++;
                        }

                        iterations++;
                    }

                    lastLoopTick = tick;

                    // Three trips round the loop's 256 iterations in each mode.
                    if (iterations >= 3 * 255)
                    {
                        mode++;
                        iterations = 0;

                        if (mode < modes.Count)
                        {
                            Select(mode);
                        }
                    }
                }
            }

            wasHigh = high;

            // The video ULA latches the byte the RAM put on the data bus during the video slot, at the
            // address the 6845 was putting out then (it moves on at the same edge).
            // In Mode 7 the RAM's row address bit 6 is MA6 exclusive-ORed with the inverted 1MHz clock, so
            // the video slot of the second half of the 1MHz cycle, which the ULA does not use, fetches the
            // neighbouring row.
            var unusedTeletextSlot = (previousMa & 0x2000) != 0 && system.VideoUla.Clock1MHz;

            if (entered && !unusedTeletextSlot && !system.VideoUla.Clock8MHz && !system.VideoUla.Clock4MHz && !system.VideoUla.Clock2MHz)
            {
                var expected = system.ReadByteDebug(system.VideoAddressFor(previousMa, previousRa));
                videoFetches++;

                if (system.DataBus != expected)
                {
                    wrongVideoBytes++;
                }

                if (expected != 0)
                {
                    changedBytes++;
                }
            }

            previousMa = system.Crtc.MA;
            previousRa = system.Crtc.RA;
        }

        await Assert.That(mode).IsEqualTo(modes.Count);
        await Assert.That(wrongIntervals).IsEqualTo(0);
        await Assert.That(videoFetches).IsGreaterThan(1000);
        await Assert.That(wrongVideoBytes).IsEqualTo(0);
        await Assert.That(changedBytes).IsGreaterThan(0);

        // Every pass stored the incremented counter, so the copies are consecutive.
        var previous = system.ReadByteDebug(0x3100);
        for (var i = 1; i < 255; i++)
        {
            var value = system.ReadByteDebug((ushort)(0x3100 + i));
            await Assert.That((byte)(value - previous)).IsEqualTo((byte)1);
            previous = value;
        }
    }
}
