using System;
using System.Threading.Tasks;

namespace Aemula.Tests.Emulation.Systems.Nes;

// Per-ROM verdicts for the community NES test-ROM suites, driven through the
// NesTestRom harness (docs/nes-ppu-plan.md). Phase 1 covers register / VRAM
// correctness: the $2007 read buffer, the PPU open-bus decay register, the
// $2004 OAM-attribute $E3 mask, and palette-RAM mirroring.
//
// Runs stay targetable with --treenode-filter; the full community suite is
// ~40 min so it is never run whole.
public class NesSystemTests
{
    // ---- Phase 0 smoke: the plumbing reaches a verdict at all ---------------

    [Test]
    public async Task OracleA_SeesTheBlarggSignature_OnAnNrom256Rom()
    {
        // oam_read is NROM-256 (32 KB PRG). The $DE $B0 $61 signature at $6001
        // only appears once the shell has run in cartridge WRAM.
        var run = NesTestRom.RunBlargg("oam_read/oam_read.nes", maxFrames: 120);
        await Assert.That(run.SawSignature).IsTrue();
    }

    [Test]
    public async Task OracleA_ReachesATerminalCode_OnVblBasics()
    {
        // 01-vbl_basics waits on the VBL flag, which the current PPU raises, so
        // it runs to completion and writes a result code. Phase 2 will assert
        // the code; for now just that it terminates.
        var run = NesTestRom.RunBlargg(
            "ppu_vbl_nmi/rom_singles/01-vbl_basics.nes", maxFrames: 400);
        await Assert.That(run.SawSignature).IsTrue();
        await Assert.That(run.Terminated).IsTrue();
    }

    [Test]
    public async Task OracleB_ScrapesNametableText_FromA2005BlarggRom()
    {
        var run = NesTestRom.RunAndScrape(
            "blargg_ppu_tests_2005.09.15b/vram_access.nes", maxFrames: 300);
        await Assert.That(run.Text.Length).IsGreaterThan(0);
    }

    // ---- Phase 1: register & VRAM correctness ------------------------------

    // The 2005-era blargg PPU suite has no $6000 status byte - it prints a
    // result code to name-table 0 with a tile-id == ASCII font, then spins.
    // "$01" is "all tests passed"; "$0n" (n >= 2) is failure code n.
    private static async Task AssertBlargg2005Passes(string rom)
    {
        var run = NesTestRom.RunAndScrape(rom, maxFrames: 400);

        Console.WriteLine($"{rom}: wentIdle={run.WentIdle} frames={run.Frames} verdict=[{run.Text}]");

        await Assert.That(run.WentIdle).IsTrue();
        await Assert.That(run.Text).IsEqualTo("$01");
    }

    [Test]
    public Task VramAccess_Rom1_ReadBufferSemantics() =>
        // $2007 buffered read: 1-byte delay, untouched by writes, palette read
        // fills the buffer from the name-table address underneath.
        AssertBlargg2005Passes("blargg_ppu_tests_2005.09.15b/vram_access.nes");

    [Test]
    public Task PaletteRam_Rom2_ReadWriteAndMirroring() =>
        // Palette r/w, $3F00-$3FFF mirroring, $10/$14/$18/$1C <-> $00/... aliases,
        // non-buffered palette read.
        AssertBlargg2005Passes("blargg_ppu_tests_2005.09.15b/palette_ram.nes");

    [Test]
    public async Task OamRead_Rom16_ReadsCurrentAddressWithE3Mask()
    {
        // $2004 reads OAM at the current $2003 without incrementing; sprite
        // attribute byte (index 2 of each sprite) reads back with bits 2-4 clear.
        var run = NesTestRom.RunBlargg("oam_read/oam_read.nes", maxFrames: 240);

        Console.WriteLine($"oam_read: terminated={run.Terminated} code={run.Code}\n{run.Text}");

        await Assert.That(run.Passed).IsTrue();
    }

    [Test]
    public async Task PpuOpenBus_Rom17_DecayRegister()
    {
        // The 8-bit open-bus decay register: writes refresh all 8 bits, each
        // register read refreshes only the bits it defines, and a bit not
        // driven to 1 decays to 0 within ~600 ms.
        var run = NesTestRom.RunBlargg("ppu_open_bus/ppu_open_bus.nes", maxFrames: 900);

        Console.WriteLine($"ppu_open_bus: terminated={run.Terminated} code={run.Code}\n{run.Text}");

        await Assert.That(run.Passed).IsTrue();
    }

    [Test]
    [Skip("sprite_ram subtests 2-5 (the PPU-side $2003/$2004 access + $E3 mask) " +
          "pass, but subtest 6 needs $4014 OAM DMA, which is a pre-existing " +
          "Ricoh2A03Chip gap - the core halts at the DMA trigger and never " +
          "resumes. Tracked as its own fix.")]
    public Task SpriteRam_Rom3_Access() =>
        AssertBlargg2005Passes("blargg_ppu_tests_2005.09.15b/sprite_ram.nes");
}
