using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Aemula.Emulation.Systems.Atari2600;

namespace Aemula.Tests.Emulation.Systems.Atari2600;

// A pixel-level regression guard on the whole TIA object / priority / timing
// stack, driven end-to-end through Atari2600System (CPU writing real TIA
// registers, TIA free-running off Osc) rather than by poking TiaChip
// directly. The cartridge is hand-assembled here as a byte array - no
// external ROM file - and paints one static scene that lights every object
// type at once: a reflected playfield, both players (distinct colours and
// graphics), both missiles (different widths / copy counts), the ball, and a
// HMOVE that shifts P0 left and M1 / BL right. The composite priority
// resolver therefore has real overlap to arbitrate on most lines.
//
// It hashes the *pre-composite* TIA raster - (Lum, Col, Blk) for every
// colour clock over one nominal frame - not Television.SampleBuffer. That is
// deliberate: the composite luma / chroma pipeline
// (Atari2600System.CompositeVideo.cs) is tracked and changed separately, and
// this test must stay stable across that work. Lum / Col are exactly what
// TIA puts on its pins before the composite stage samples them.
//
// Regenerating the golden after an intentional TIA change: run this test; on
// mismatch it writes the captured raster to a temp file and prints the
// freshly computed hash. Paste that value into ExpectedRasterHash below.
public class Atari2600SystemVideoTests
{
    // Cartridge code sits at the bottom of the 4K cartridge-selected window,
    // mirrored across the 2K image (Cartridge2K), same as the other
    // Atari2600SystemTests cartridges.
    private const ushort CodeStart = 0x1000;

    // NTSC 2600 frame: 262 scanlines x 228 colour clocks. One
    // Atari2600System.Tick() is one colour clock (one OSC pulse into TIA).
    private const int ColourClocksPerFrame = 262 * 228;

    // Frames of warm-up before the captured frame. The scene is fully static
    // after the straight-line setup drops into its JMP-to-self loop, so this
    // only has to get past that setup and let TIA's timing settle.
    private const int WarmUpFrames = 4;

    // FNV-1a 64-bit of the captured raster byte stream (Lum, Col, Blk-as-0/1
    // per colour clock). Regenerate as described in the class remarks.
    private const ulong ExpectedRasterHash = 0x7E5E878274FC3E99UL;

    private const ulong Fnv1a64OffsetBasis = 14695981039346656037UL;
    private const ulong Fnv1a64Prime = 1099511628211UL;

    // Straight-line TIA setup: colours, playfield, sizes, graphics, object
    // enables, five RESx strobes spaced by NOPs so the objects land at
    // different columns, then horizontal motion + a single HMOVE, then a
    // JMP-to-self. TIA holds all of this, so every frame after the setup
    // renders the same picture.
    private static byte[] BuildMultiObjectCartridge()
    {
        var code = new List<byte>
        {
            0x78,             // SEI
            0xD8,             // CLD
            0xA2, 0xFF,       // LDX #$FF
            0x9A,             // TXS
            0xA9, 0x00,       // LDA #$00
            0x95, 0x00,       // clr: STA $00,X
            0xCA,             // DEX
            0xD0, 0xFB,       // BNE clr

            0xA9, 0x1E, 0x85, 0x06, // COLUP0 = $1E
            0xA9, 0x66, 0x85, 0x07, // COLUP1 = $66
            0xA9, 0x46, 0x85, 0x08, // COLUPF = $46
            0xA9, 0x00, 0x85, 0x09, // COLUBK = $00

            0xA9, 0xF0, 0x85, 0x0D, // PF0 = $F0
            0xA9, 0xA5, 0x85, 0x0E, // PF1 = $A5
            0xA9, 0xC3, 0x85, 0x0F, // PF2 = $C3
            0xA9, 0x21, 0x85, 0x0A, // CTRLPF = $21 (D0 reflect, ball size 4)

            0xA9, 0x23, 0x85, 0x04, // NUSIZ0 = $23 (missile width 4, 3 copies close)
            0xA9, 0x10, 0x85, 0x05, // NUSIZ1 = $10 (missile width 2, one copy)

            0xA9, 0x3C, 0x85, 0x1B, // GRP0 = $3C
            0xA9, 0x7E, 0x85, 0x1C, // GRP1 = $7E

            0xA9, 0x02,       // LDA #$02  (D1 for the enables; value is irrelevant to the RESx strobes)
            0x85, 0x1D,       // ENAM0
            0x85, 0x1E,       // ENAM1
            0x85, 0x1F,       // ENABL

            0x85, 0x10,       // RESP0
            0xEA, 0xEA,       // NOP NOP
            0x85, 0x11,       // RESP1
            0xEA, 0xEA,       // NOP NOP
            0x85, 0x12,       // RESM0
            0xEA, 0xEA,       // NOP NOP
            0x85, 0x13,       // RESM1
            0xEA, 0xEA,       // NOP NOP
            0x85, 0x14,       // RESBL

            0xA9, 0x70, 0x85, 0x20, // HMP0 = $70 (+7, left)
            0xA9, 0x90, 0x85, 0x23, // HMM1 = $90 (-7, right)
            0xA9, 0xA0, 0x85, 0x24, // HMBL = $A0 (-6, right)
            0x85, 0x2A,       // HMOVE strobe
        };

        // loop: JMP loop
        var loopAddress = CodeStart + code.Count;
        code.Add(0x4C);
        code.Add((byte)(loopAddress & 0xFF));
        code.Add((byte)(loopAddress >> 8));

        var rom = new byte[2048];
        code.CopyTo(rom);

        // Reset vector ($FFFC/$FFFD) -> CodeStart, via the 2K image's
        // $07FC/$07FD (Cartridge2K mirrors the 2K image across the 4K
        // window), same as the other Atari2600SystemTests cartridges.
        rom[0x7FC] = (byte)(CodeStart & 0xFF);
        rom[0x7FD] = (byte)(CodeStart >> 8);

        return rom;
    }

    private static string WriteCartridgeToTempFile(byte[] rom)
    {
        var path = Path.Combine(Path.GetTempPath(), $"aemula-atari2600-video-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, rom);
        return path;
    }

    [Test]
    public async Task MultiObjectScenePreCompositeRasterMatchesGolden()
    {
        var system = new Atari2600System();
        var path = WriteCartridgeToTempFile(BuildMultiObjectCartridge());

        try
        {
            system.LoadProgram(path);
            system.Reset();

            for (var i = 0; i < WarmUpFrames * ColourClocksPerFrame; i++)
            {
                system.Tick();
            }

            var raster = new byte[ColourClocksPerFrame * 3];
            var hash = Fnv1a64OffsetBasis;

            for (var clock = 0; clock < ColourClocksPerFrame; clock++)
            {
                system.Tick();

                var tia = system.Tia;
                var lum = tia.Lum;
                var col = tia.Col;
                var blk = (byte)(tia.Blk ? 1 : 0);

                raster[clock * 3] = lum;
                raster[clock * 3 + 1] = col;
                raster[clock * 3 + 2] = blk;

                hash = (hash ^ lum) * Fnv1a64Prime;
                hash = (hash ^ col) * Fnv1a64Prime;
                hash = (hash ^ blk) * Fnv1a64Prime;
            }

            if (hash != ExpectedRasterHash)
            {
                var dumpPath = Path.Combine(
                    Path.GetTempPath(), $"aemula-multiobject-tia-raster-{Guid.NewGuid():N}.bin");
                File.WriteAllBytes(dumpPath, raster);

                Assert.Fail(
                    $"Pre-composite TIA raster hash mismatch. Computed 0x{hash:X16}, " +
                    $"expected 0x{ExpectedRasterHash:X16}. Captured raster written to {dumpPath}. " +
                    "If this change to TIA was intentional, set ExpectedRasterHash to the computed value.");
            }

            await Assert.That(hash).IsEqualTo(ExpectedRasterHash);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
