using System;
using System.Threading.Tasks;

namespace Aemula.Tests.Emulation.Systems.Nes;

// End-to-end checks that the NesTestRom harness reaches a verdict against the
// real community ROMs. Phase 0 (docs/nes-ppu-plan.md) only proves the plumbing:
// a ROM boots far enough to report through its oracle. The per-suite pass/fail
// assertions arrive with Phases 1-5.
public class NesSystemTests
{
    [Test]
    public async Task OracleA_SeesTheBlarggSignature_OnAnNrom256Rom()
    {
        // oam_read is NROM-256 (32 KB PRG). The $DE $B0 $61 signature at $6001
        // only appears once the shell has run in cartridge WRAM - so seeing it
        // proves 32 KB PRG mapping + $6000-$7FFF WRAM + CPU execution.
        var run = NesTestRom.RunBlargg("oam_read/oam_read.nes", maxFrames: 120);

        Console.WriteLine($"oam_read: signature={run.SawSignature} terminated={run.Terminated} " +
            $"code={run.Code} frames={run.Frames}\n{run.Text}");

        await Assert.That(run.SawSignature).IsTrue();
    }

    [Test]
    public async Task OracleA_ReachesATerminalCode_OnVblBasics()
    {
        // 01-vbl_basics is NROM-128 + CHR RAM. It waits on the VBL flag, which
        // the current PPU does raise, so the test runs to completion and writes
        // a result code - pass or fail. Phase 0 only asserts it terminates.
        var run = NesTestRom.RunBlargg(
            "ppu_vbl_nmi/rom_singles/01-vbl_basics.nes", maxFrames: 400);

        Console.WriteLine($"01-vbl_basics: terminated={run.Terminated} code={run.Code} " +
            $"frames={run.Frames}\n{run.Text}");

        await Assert.That(run.SawSignature).IsTrue();
        await Assert.That(run.Terminated).IsTrue();
    }

    [Test]
    public async Task OracleB_ScrapesNametableText_FromA2005BlarggRom()
    {
        // vram_access is NROM-128 + CHR RAM; it uploads an ASCII-mapped font to
        // CHR RAM and prints its verdict into name-table 0. A non-empty scrape
        // proves CHR RAM writes, name-table mirroring, and the scrape path.
        var run = NesTestRom.RunAndScrape(
            "blargg_ppu_tests_2005.09.15b/vram_access.nes", maxFrames: 300);

        Console.WriteLine($"vram_access: wentIdle={run.WentIdle} frames={run.Frames}\n{run.Text}");

        await Assert.That(run.Text.Length).IsGreaterThan(0);
    }
}
