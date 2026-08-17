using System.Threading.Tasks;
using Aemula.Emulation.Systems.AppleII;

namespace Aemula.Tests.Emulation.Systems.AppleII;

// Phase 5: the $C050-$C057 screen mode soft switches, LORES color block
// generation, and HIRES addressing/shifting/PAGE2, cross-checked against
// Jim Sather's "Understanding the Apple II" chapters 5, 7, and 8.
public class AppleIISystemVideoModesTests
{
    // Ticks enough to get the Autostart ROM through its boot sequence and
    // into its idle input-wait loop, the same budget
    // AppleIISystemTests.KeyPressReachesKeyboardLatch uses - important here
    // because the ROM's own boot code sets TEXT mode, so screen-mode soft
    // switches must be poked *after* boot, not before, or the ROM will
    // stomp them.
    private static void BootToIdle(AppleIISystem system)
    {
        for (var i = 0; i < 500_000; i++)
        {
            system.Tick();
        }
    }

    private static void TickOneFrame(AppleIISystem system)
    {
        // 262 lines * 65 H-states * 14 (or occasionally 16) master ticks
        // per PHASE0 is ~238,000 ticks; comfortably over-tick to guarantee
        // at least one full frame renders.
        for (var i = 0; i < 400_000; i++)
        {
            system.Tick();
        }
    }

    private static bool DisplayContainsColor(AppleIISystem system, byte r, byte g, byte b)
    {
        foreach (var pixel in system.Display.Data)
        {
            if (pixel.R == r && pixel.G == g && pixel.B == b)
            {
                return true;
            }
        }

        return false;
    }

    [Test]
    public async Task ModeSwitchesSurviveInterleavedUnrelatedAccesses()
    {
        // The address-decode chain now drives the mode-switch latch's
        // A0-A2/D/G pins on *every* CPU access, not just ones in
        // $C050-$C05F (SetModeSwitchLatchAddress in AppleIISystem.Video.cs)
        // - unrelated accesses must leave the latch alone. This is the
        // specific hazard the "disable G first" ordering in that method
        // guards against: without it, an unrelated access's address bits
        // could transiently glitch through while G was still asserted from
        // a previous $C050-$C05F access.
        var system = new AppleIISystem();
        system.LoadProgram("");

        BootToIdle(system);

        system.WriteByteDebug(0xC050, 0); // GRAPHICS
        system.WriteByteDebug(0xC057, 0); // HIRES
        system.WriteByteDebug(0xC055, 0); // PAGE2

        for (var address = 0; address < 0x100; address++)
        {
            system.ReadByteDebug((ushort)address); // RAM
            system.WriteByteDebug((ushort)address, 0);
            system.ReadByteDebug((ushort)(0xD000 + address)); // ROM
            system.ReadByteDebug((ushort)(0xC800 + address)); // "seventh ROM" / open bus
        }

        var (textMode, _, page2Mode, hiresMode) = system.GetModeSwitchesForTests();

        await Assert.That(textMode).IsFalse();
        await Assert.That(hiresMode).IsTrue();
        await Assert.That(page2Mode).IsTrue();
    }

    [Test]
    public async Task SeventhRomRangeReadsAsOpenBus()
    {
        // $C800-$CFFF is F12's Y1 - a separate signal from the I/O Section
        // (Y0) that the mode-switch/keyboard checks live under. No slot
        // cards are implemented, so it should just be open bus, distinctly
        // from (not accidentally aliased with) the I/O Section handling.
        var system = new AppleIISystem();
        system.LoadProgram("");

        await Assert.That(system.ReadByteDebug(0xC800)).IsEqualTo((byte)0xFF);
        await Assert.That(system.ReadByteDebug(0xCFFF)).IsEqualTo((byte)0xFF);
    }

    [Test]
    public async Task LoresColorBlockRendersFromScreenNibbles()
    {
        var system = new AppleIISystem();
        system.LoadProgram("");

        BootToIdle(system);

        system.WriteByteDebug(0xC050, 0); // GRAPHICS
        system.WriteByteDebug(0xC056, 0); // LORES
        system.WriteByteDebug(0xC054, 0); // PAGE1

        // Fill all of PAGE1 TEXT/LORES memory with a byte whose low nibble
        // is color 1 (magenta) and high nibble is color 2 (dark blue), so
        // the test doesn't need to reproduce the scrambled address formula
        // to find a byte the scanner will actually read.
        for (var address = 0x400; address <= 0x7FF; address++)
        {
            system.WriteByteDebug((ushort)address, 0x21);
        }

        TickOneFrame(system);

        // LoresPalette[1] (magenta) and LoresPalette[2] (dark blue) in
        // AppleIISystem.Video.cs.
        await Assert.That(DisplayContainsColor(system, 0xFF, 0x00, 0x8C)).IsTrue();
        await Assert.That(DisplayContainsColor(system, 0x15, 0x10, 0xFF)).IsTrue();
    }

    [Test]
    public async Task Page2SwitchesLoresAddressSource()
    {
        var system = new AppleIISystem();
        system.LoadProgram("");

        BootToIdle(system);

        system.WriteByteDebug(0xC050, 0); // GRAPHICS
        system.WriteByteDebug(0xC056, 0); // LORES

        // PAGE1 memory gets color 1 (magenta); PAGE2 memory gets color 12
        // (light green).
        for (var address = 0x400; address <= 0x7FF; address++)
        {
            system.WriteByteDebug((ushort)address, 0x11);
        }

        for (var address = 0x800; address <= 0xBFF; address++)
        {
            system.WriteByteDebug((ushort)address, 0xCC);
        }

        system.WriteByteDebug(0xC054, 0); // PAGE1
        TickOneFrame(system);

        await Assert.That(DisplayContainsColor(system, 0xFF, 0x00, 0x8C)).IsTrue();
        await Assert.That(DisplayContainsColor(system, 0x00, 0xFF, 0x00)).IsFalse();

        system.WriteByteDebug(0xC055, 0); // PAGE2

        // Every visible position is re-fetched and redrawn every frame, so
        // one more frame is enough for PAGE2's color to fully replace
        // PAGE1's at every position that was showing it.
        TickOneFrame(system);

        await Assert.That(DisplayContainsColor(system, 0x00, 0xFF, 0x00)).IsTrue();
        await Assert.That(DisplayContainsColor(system, 0xFF, 0x00, 0x8C)).IsFalse();
    }

    [Test]
    public async Task HiresBitZeroIsLeftmostDot()
    {
        var system = new AppleIISystem();
        system.LoadProgram("");

        BootToIdle(system);

        system.WriteByteDebug(0xC050, 0); // GRAPHICS
        system.WriteByteDebug(0xC057, 0); // HIRES
        system.WriteByteDebug(0xC054, 0); // PAGE1

        // Fill all of HIRES PAGE1 ($2000-$3FFF) with the same byte so every
        // 7-dot cell the scanner reads shows the identical bit pattern:
        // bit0=1, bit1=0, bit2=1, bit3=0, bit4=1, bit5=0, bit6=1.
        for (var address = 0x2000; address <= 0x3FFF; address++)
        {
            system.WriteByteDebug((ushort)address, 0b0101_0101);
        }

        TickOneFrame(system);

        var expectedLit = new[] { true, false, true, false, true, false, true };

        // Row 100 is comfortably inside the visible 192-line HIRES picture,
        // away from any HBL/VBL edge effects; every 7-pixel cell across
        // this row should show the same pattern, since every HIRES byte in
        // memory is identical.
        var rowStart = 100 * (int)system.Display.Width;

        for (var dot = 0; dot < 7; dot++)
        {
            var pixel = system.Display.Data[rowStart + dot];
            var lit = pixel.R != 0;
            await Assert.That(lit).IsEqualTo(expectedLit[dot]);
        }
    }

    [Test]
    public async Task HiresColorPhaseFollowsColumnParityAndDl7()
    {
        var system = new AppleIISystem();
        system.LoadProgram("");

        BootToIdle(system);

        system.WriteByteDebug(0xC050, 0); // GRAPHICS
        system.WriteByteDebug(0xC057, 0); // HIRES
        system.WriteByteDebug(0xC054, 0); // PAGE1

        // DL7 (bit 7) clear: even columns should be phase 0, odd phase 2.
        for (var address = 0x2000; address <= 0x3FFF; address++)
        {
            system.WriteByteDebug((ushort)address, 0b0_1010101);
        }

        TickOneFrame(system);

        var rowStart = 100 * (int)system.Display.Width;
        var expectedPhasesDl7Clear = new byte[] { 0, 2, 0, 2, 0, 2, 0 };

        for (var dot = 0; dot < 7; dot++)
        {
            await Assert.That(system.HiresColorPhase[rowStart + dot]).IsEqualTo(expectedPhasesDl7Clear[dot]);
        }

        // DL7 set: even columns should be phase 1, odd phase 3.
        for (var address = 0x2000; address <= 0x3FFF; address++)
        {
            system.WriteByteDebug((ushort)address, 0b1_1010101);
        }

        TickOneFrame(system);

        var expectedPhasesDl7Set = new byte[] { 1, 3, 1, 3, 1, 3, 1 };

        for (var dot = 0; dot < 7; dot++)
        {
            await Assert.That(system.HiresColorPhase[rowStart + dot]).IsEqualTo(expectedPhasesDl7Set[dot]);
        }
    }

    [Test]
    public async Task VideoDataBitIsForcedLowDuringBlanking()
    {
        // docs/apple-ii-ntsc-video-plan.md phase 2: matches Gayler's "A9"
        // blanking-gated video-data selector - no mode setup needed, HBL
        // occurs every line regardless of mode.
        var system = new AppleIISystem();
        system.LoadProgram("");

        var wasPhase0 = system.Phase0;
        var sampled = false;

        for (var i = 0; i < 5000 && !sampled; i++)
        {
            system.Tick();
            var isPhase0 = system.Phase0;

            if (isPhase0 && !wasPhase0 && system.Hbl)
            {
                foreach (var bit in system.GetVideoDataBitsForTests())
                {
                    await Assert.That(bit).IsFalse();
                }

                sampled = true;
            }

            wasPhase0 = isPhase0;
        }

        await Assert.That(sampled).IsTrue();
    }

    [Test]
    public async Task VideoDataBitMatchesHiresShiftedPattern()
    {
        // Cross-checks _videoDataBits against the same known byte pattern
        // HiresBitZeroIsLeftmostDot uses for Display, since both are set
        // from the same "lit" value in DrawHiresByte.
        var system = new AppleIISystem();
        system.LoadProgram("");

        BootToIdle(system);

        system.WriteByteDebug(0xC050, 0); // GRAPHICS
        system.WriteByteDebug(0xC057, 0); // HIRES
        system.WriteByteDebug(0xC054, 0); // PAGE1

        for (var address = 0x2000; address <= 0x3FFF; address++)
        {
            system.WriteByteDebug((ushort)address, 0b0101_0101);
        }

        var expectedLit = new[] { true, false, true, false, true, false, true };
        var wasPhase0 = system.Phase0;
        var sampled = false;

        for (var i = 0; i < 400_000 && !sampled; i++)
        {
            system.Tick();
            var isPhase0 = system.Phase0;

            if (isPhase0 && !wasPhase0 && !system.Hbl && !system.Vbl)
            {
                var bits = system.GetVideoDataBitsForTests();

                for (var dot = 0; dot < 7; dot++)
                {
                    await Assert.That(bits[dot]).IsEqualTo(expectedLit[dot]);
                }

                sampled = true;
            }

            wasPhase0 = isPhase0;
        }

        await Assert.That(sampled).IsTrue();
    }

    [Test]
    public async Task VideoDataBitMatchesLoresCirculatingNibblePattern()
    {
        // docs/apple-ii-ntsc-video-plan.md phase 2: LORES's real VIDEO DATA
        // line is the active nibble circulated as a period-4 pattern,
        // indexed by absolute pixel x (not dot-within-cell) so the phase
        // carries continuously across byte boundaries (Sather p.8-8).
        var system = new AppleIISystem();
        system.LoadProgram("");

        BootToIdle(system);

        system.WriteByteDebug(0xC050, 0); // GRAPHICS
        system.WriteByteDebug(0xC056, 0); // LORES
        system.WriteByteDebug(0xC054, 0); // PAGE1

        // Same nibble in both halves of the byte, so the sampled dot's
        // expected pattern doesn't depend on which half (VC) is active.
        const byte nibble = 0x5; // 0b0101
        for (var address = 0x400; address <= 0x7FF; address++)
        {
            system.WriteByteDebug((ushort)address, (byte)(nibble | (nibble << 4)));
        }

        var wasPhase0 = system.Phase0;
        var sampled = false;

        for (var i = 0; i < 400_000 && !sampled; i++)
        {
            system.Tick();
            var isPhase0 = system.Phase0;

            if (isPhase0 && !wasPhase0 && !system.Hbl && !system.Vbl)
            {
                var (h, _) = system.GetVideoScannerStateForTests();
                var rawH = h & 0b0_111111; // H0-H5, masking off HPE'
                var baseX = (rawH - 24) * 7;

                var bits = system.GetVideoDataBitsForTests();

                for (var dot = 0; dot < 7; dot++)
                {
                    var x = baseX + dot;
                    var expected = ((nibble >> (x & 3)) & 1) != 0;
                    await Assert.That(bits[dot]).IsEqualTo(expected);
                }

                sampled = true;
            }

            wasPhase0 = isPhase0;
        }

        await Assert.That(sampled).IsTrue();
    }

    [Test]
    public async Task MixModeShowsTextForBottomFourRows()
    {
        var system = new AppleIISystem();
        system.LoadProgram("");

        BootToIdle(system);

        system.WriteByteDebug(0xC050, 0); // GRAPHICS
        system.WriteByteDebug(0xC053, 0); // MIX
        system.WriteByteDebug(0xC056, 0); // LORES
        system.WriteByteDebug(0xC054, 0); // PAGE1

        // TEXT and LORES share the same memory, so this single fill feeds
        // both interpretations: as LORES color in the top 160 lines, and as
        // a (mostly monochrome) glyph in the bottom four text rows.
        for (var address = 0x400; address <= 0x7FF; address++)
        {
            system.WriteByteDebug((ushort)address, 0x21);
        }

        TickOneFrame(system);

        // Comfortably inside the graphics region: should be a solid LORES
        // color, not gray.
        var topPixel = system.Display.Data[50 * (int)system.Display.Width + 10];
        var topIsColor = topPixel.R != topPixel.G || topPixel.G != topPixel.B;
        await Assert.That(topIsColor).IsTrue();

        // Scan lines 160-191 (Sather p.5-14: "V4.V2 actually identifies
        // scan lines 160 through 191") are the bottom four text rows -
        // TEXT's black/white/gray rendering, not a LORES color.
        var sawGrayInBottomRegion = false;

        for (var y = 160; y < 192 && !sawGrayInBottomRegion; y++)
        {
            var rowStart = y * (int)system.Display.Width;

            for (var x = 0; x < system.Display.Width; x++)
            {
                var pixel = system.Display.Data[rowStart + x];

                if (pixel.R == pixel.G && pixel.G == pixel.B)
                {
                    sawGrayInBottomRegion = true;
                    break;
                }
            }
        }

        await Assert.That(sawGrayInBottomRegion).IsTrue();
    }
}
