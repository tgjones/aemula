using System;
using System.Threading.Tasks;

namespace Aemula.Tests.Emulation.Systems.Nes;

// Per-ROM verdicts for the community NES test-ROM suites, driven through the
// NesTestRom harness. What is covered so far: register / VRAM correctness (the
// $2007 read buffer, the PPU open-bus decay register, the $2004 OAM-attribute
// $E3 mask, palette-RAM mirroring) and VBL / NMI timing.
//
// Runs stay targetable with --treenode-filter; the full community suite is
// ~40 min so it is never run whole.
public class NesSystemTests
{
    // ---- Smoke: the plumbing reaches a verdict at all -----------------------

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

    // ---- Register & VRAM correctness ---------------------------------------

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
    public Task SpriteRam_Rom3_Access() =>
        // $2003/$2004 access, no increment on read, increment on write, the $E3
        // attribute mask, and a full $4014 OAM DMA.
        AssertBlargg2005Passes("blargg_ppu_tests_2005.09.15b/sprite_ram.nes");

    // ---- VBL / NMI timing (ppu_vbl_nmi) ------------------------------------

    private static async Task AssertBlarggPasses(string rom, int maxFrames)
    {
        var run = NesTestRom.RunBlargg(rom, maxFrames);

        Console.WriteLine(
            $"{rom}: terminated={run.Terminated} code={run.Code} frames={run.Frames}\n{run.Text}");

        await Assert.That(run.Passed).IsTrue();
    }

    [Test]
    public Task VblNmi_01_VblBasics() =>
        // Length of the VBL period, $2002 mirrored every 8 bytes, flag cleared
        // on read, and the BG-off period.
        AssertBlarggPasses("ppu_vbl_nmi/rom_singles/01-vbl_basics.nes", 600);

    [Test]
    public Task VblNmi_02_VblSetTime() =>
        // The exact dot the VBL flag is set on, to 1-dot resolution - including
        // the read one dot earlier that cancels the flag for the whole frame.
        AssertBlarggPasses("ppu_vbl_nmi/rom_singles/02-vbl_set_time.nes", 600);

    [Test]
    public Task VblNmi_03_VblClearTime() =>
        // The exact dot the VBL flag is cleared on.
        AssertBlarggPasses("ppu_vbl_nmi/rom_singles/03-vbl_clear_time.nes", 600);

    [Test]
    public Task VblNmi_04_NmiControl() =>
        // NMI when enabled while the flag is already set, $2000 mirroring, no
        // second NMI from re-writing $80, "after the NEXT instruction".
        AssertBlarggPasses("ppu_vbl_nmi/rom_singles/04-nmi_control.nes", 600);

    [Test]
    public Task VblNmi_05_NmiTiming() =>
        // Delivery latency of the NMI relative to the instruction boundary.
        AssertBlarggPasses("ppu_vbl_nmi/rom_singles/05-nmi_timing.nes", 600);

    [Test]
    public Task VblNmi_06_Suppression() =>
        // Reading $2002 around the set dot: one dot early reads clear and kills
        // the flag; on the dot or just after reads set but suppresses the NMI.
        AssertBlarggPasses("ppu_vbl_nmi/rom_singles/06-suppression.nes", 600);

    [Test]
    public Task VblNmi_07_NmiOnTiming() =>
        // Enabling NMI within a couple of dots of the flag being set.
        AssertBlarggPasses("ppu_vbl_nmi/rom_singles/07-nmi_on_timing.nes", 600);

    [Test]
    public Task VblNmi_08_NmiOffTiming() =>
        // Disabling NMI around the set dot - the /NMI pulse is then too short
        // for the CPU's once-per-cycle edge detector to see.
        AssertBlarggPasses("ppu_vbl_nmi/rom_singles/08-nmi_off_timing.nes", 600);

    [Test]
    public Task VblNmi_09_EvenOddFrames() =>
        // Whether the odd field's pre-render dot is dropped, against the pattern
        // of BG enables.
        AssertBlarggPasses("ppu_vbl_nmi/rom_singles/09-even_odd_frames.nes", 600);

    [Test]
    public Task VblNmi_10_EvenOddTiming() =>
        // Which PPU clock the drop decision samples the rendering bits on.
        AssertBlarggPasses("ppu_vbl_nmi/rom_singles/10-even_odd_timing.nes", 900);

    [Test]
    public Task VblClearTime_Blargg2005() =>
        // Coarse check that the VBL flag clears ~2270 CPU clocks after the NMI.
        AssertBlargg2005Passes("blargg_ppu_tests_2005.09.15b/vbl_clear_time.nes");

    // ---- Sprite pipeline + sprite-0 hit (sprite_hit_tests_2005.10.05) -----

    // These render "PASSED" / "FAILED #n" to name-table 0 with an ASCII font
    // (tile id == char code), then spin.
    private static async Task AssertSpriteHitPasses(string rom, int maxFrames = 600)
    {
        var run = NesTestRom.RunAndScrape(rom, maxFrames);

        Console.WriteLine($"{rom}: wentIdle={run.WentIdle} frames={run.Frames}\n{run.Text}");

        await Assert.That(run.WentIdle).IsTrue();
        await Assert.That(run.Passed).IsTrue();
    }

    [Test]
    public Task SpriteHit_01_Basics() =>
        // Sprite-0 hit fires behind BG, and misses when either layer is off,
        // the overlapping pixels are transparent, or only other sprites overlap.
        AssertSpriteHitPasses("sprite_hit_tests_2005.10.05/01.basics.nes");

    [Test]
    public Task SpriteHit_02_Alignment() =>
        // Pixel-exact hit alignment of sprite vs. BG on all four edges.
        AssertSpriteHitPasses("sprite_hit_tests_2005.10.05/02.alignment.nes");

    [Test]
    public Task SpriteHit_09_TimingBasics() =>
        // $2002 bit 6 is set at the right dot within the scanline.
        AssertSpriteHitPasses("sprite_hit_tests_2005.10.05/09.timing_basics.nes");

    // ---- Sprite overflow (sprite_overflow_tests) -------------------------------

    // Same on-screen "PASSED" / "FAILED: #n" font as the sprite-hit suite, then
    // a forever loop.
    [Test]
    public async Task SpriteOverflow_1_Basics()
    {
        // $2002 bit 5 is set when a 9th sprite falls on a scanline, is not
        // cleared by reading $2002, is cleared at the end of VBL, and is only
        // evaluated while rendering is enabled ($2001 bits 3-4).
        var run = NesTestRom.RunAndScrape(
            "sprite_overflow_tests/1.Basics.nes", maxFrames: 600);

        Console.WriteLine($"sprite_overflow 1.Basics: wentIdle={run.WentIdle} frames={run.Frames}\n{run.Text}");

        await Assert.That(run.WentIdle).IsTrue();
        await Assert.That(run.Passed).IsTrue();
    }
}
