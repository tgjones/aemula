using System;
using System.IO;
using System.Text;
using Aemula.Emulation.Systems.Nes;

namespace Aemula.Tests.Emulation.Systems.Nes;

// Shared harness for the community NES test ROMs (github.com/christopherpow/
// nes-test-roms), copied under Assets/nes-test-roms. Two result oracles, per
// docs/nes-ppu-plan.md:
//
//   A. Modern blargg protocol - status byte at $6000, "$DE $B0 $61" signature
//      at $6001-$6003, NUL-terminated ASCII log from $6004. Confirmed in
//      ppu_vbl_nmi/source/common/text_out.s.
//
//   B. Name-table text scrape - the 2005-era suites render "PASSED" / "FAILED
//      #n" with an ASCII-mapped CHR font (tile id == char code) then spin
//      forever. Read CIRAM back, map tiles to ASCII.
//
// All runs disable the composite-video decode (NesSystem.DecodeVideo = false):
// the oracles read memory, never the Television, and the NTSC FIR is the
// measured hot path.
internal static class NesTestRom
{
    private const string AssetsRoot = "nes-test-roms";

    public static string Path(string relative) => System.IO.Path.Combine(
        "Emulation", "Systems", "Nes", "Assets", AssetsRoot,
        relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public static NesSystem Load(string relativeRomPath)
    {
        var nes = new NesSystem { DecodeVideo = false };
        nes.LoadProgram(Path(relativeRomPath));
        return nes;
    }

    // ---- Oracle A: blargg $6000 protocol ------------------------------------

    public readonly record struct BlarggRun(
        bool SawSignature,
        bool Terminated,
        int Code,
        string Text,
        long Frames)
    {
        public bool Passed => Terminated && Code == 0;
    }

    public static BlarggRun RunBlargg(string relativeRomPath, int maxFrames = 1200)
    {
        var nes = Load(relativeRomPath);
        return RunBlargg(nes, maxFrames);
    }

    public static BlarggRun RunBlargg(NesSystem nes, int maxFrames = 1200)
    {
        var startFrame = nes.Ppu.Frames;
        var sawSignature = false;
        var ticks = 0L;
        var resetHoldUntil = 0L;

        while (nes.Ppu.Frames - startFrame < (ulong)maxFrames)
        {
            nes.Tick();
            ticks++;

            var frames = (long)(nes.Ppu.Frames - startFrame);

            if (resetHoldUntil != 0)
            {
                if (ticks >= resetHoldUntil)
                {
                    resetHoldUntil = 0;
                    nes.Reset();
                }
                continue;
            }

            if (!sawSignature)
            {
                if (nes.ReadByteDebug(0x6001) == 0xDE &&
                    nes.ReadByteDebug(0x6002) == 0xB0 &&
                    nes.ReadByteDebug(0x6003) == 0x61)
                {
                    sawSignature = true;
                }
                continue;
            }

            var status = nes.ReadByteDebug(0x6000);
            if (status == 0x81)
            {
                // "Needs reset, delayed >= 100 ms." ~150 ms of master ticks.
                resetHoldUntil = ticks + 3_200_000;
                continue;
            }

            if (status < 0x80)
            {
                return new BlarggRun(true, true, status, ReadZString(nes, 0x6004), frames);
            }
        }

        return new BlarggRun(
            sawSignature,
            false,
            -1,
            sawSignature ? ReadZString(nes, 0x6004) : "(no $DE B0 61 signature seen)",
            (long)(nes.Ppu.Frames - startFrame));
    }

    private static string ReadZString(NesSystem nes, ushort address)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 0x1000; i++)
        {
            var b = nes.ReadByteDebug((ushort)(address + i));
            if (b == 0)
            {
                break;
            }
            sb.Append((char)b);
        }
        return sb.ToString().Trim();
    }

    // ---- Oracle B: name-table text scrape ---------------------------------

    public readonly record struct ScrapeRun(bool WentIdle, string Text, long Frames)
    {
        public bool Passed =>
            Text.Contains("PASSED", StringComparison.OrdinalIgnoreCase) &&
            !Text.Contains("FAILED", StringComparison.OrdinalIgnoreCase);
    }

    // Runs until the CPU parks in a tight loop (the blargg "forever") or the cap
    // is hit, then reads name-table 0 back as text. The park has to hold for the
    // whole sample window: the sprite-hit suites run dozens of sub-tests, each
    // ending in a short $2002 poll loop, and a handful of those samples in a row
    // is not the end of the ROM - only the final "forever" jmp stays put.
    public static ScrapeRun RunAndScrape(
        string relativeRomPath, int maxFrames = 600, int minFrames = 45)
    {
        var nes = Load(relativeRomPath);
        var startFrame = nes.Ppu.Frames;

        Span<ushort> recentPc = stackalloc ushort[32];
        var pcIndex = 0;
        var pcFilled = 0;
        var wentIdle = false;
        var lastSampleFrame = nes.Ppu.Frames;

        while (nes.Ppu.Frames - startFrame < (ulong)maxFrames)
        {
            nes.Tick();

            if (nes.Ppu.Frames == lastSampleFrame)
            {
                continue;
            }
            lastSampleFrame = nes.Ppu.Frames;

            recentPc[pcIndex] = nes.Cpu.PC;
            pcIndex = (pcIndex + 1) % recentPc.Length;
            pcFilled = Math.Min(pcFilled + 1, recentPc.Length);

            if ((long)(nes.Ppu.Frames - startFrame) < minFrames || pcFilled < recentPc.Length)
            {
                continue;
            }

            ushort lo = ushort.MaxValue, hi = 0;
            foreach (var pc in recentPc)
            {
                lo = Math.Min(lo, pc);
                hi = Math.Max(hi, pc);
            }

            if (hi - lo <= 12)
            {
                wentIdle = true;
                break;
            }
        }

        return new ScrapeRun(wentIdle, ScrapeNametable(nes), (long)(nes.Ppu.Frames - startFrame));
    }

    // Name-table 0 ($2000, 32x30 tiles) as text. The 2005 blargg suites use a
    // font whose tile id is the ASCII code, so a tile byte is its own character.
    public static string ScrapeNametable(NesSystem nes)
    {
        var sb = new StringBuilder();
        for (var row = 0; row < 30; row++)
        {
            var line = new StringBuilder();
            for (var col = 0; col < 32; col++)
            {
                var tile = nes.PeekCiram((ushort)(0x2000 + row * 32 + col));
                line.Append(tile is >= 0x20 and < 0x7F ? (char)tile : ' ');
            }

            var trimmed = line.ToString().TrimEnd();
            if (trimmed.Length > 0)
            {
                sb.AppendLine(trimmed);
            }
        }
        return sb.ToString().Trim();
    }
}
