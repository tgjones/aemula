using System.Threading.Tasks;
using Aemula.Emulation.Systems.AppleII;

namespace Aemula.Tests.Emulation.Systems.AppleII;

// The $C050-$C057 screen mode soft switches, LORES color block
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

        await Assert.That(system.ReadByteDebug(0xC800)).IsEqualTo((byte)0xFF);
        await Assert.That(system.ReadByteDebug(0xCFFF)).IsEqualTo((byte)0xFF);
    }

    [Test]
    public async Task LoresNibblesDriveTheVideoDataLine()
    {
        var system = new AppleIISystem();

        BootToIdle(system);

        system.WriteByteDebug(0xC050, 0); // GRAPHICS
        system.WriteByteDebug(0xC056, 0); // LORES
        system.WriteByteDebug(0xC054, 0); // PAGE1

        // Low nibble 0 (upper half of each 8-line text row) and high
        // nibble F (lower half): black is a dark line, white a solid one.
        // Filling all of PAGE1 avoids reproducing the scrambled address
        // formula to find a byte the scanner will actually read.
        for (var address = 0x400; address <= 0x7FF; address++)
        {
            system.WriteByteDebug((ushort)address, 0xF0);
        }

        var frame = AppleIIVideoFrame.Capture(system);

        // Lines 0-3 of each text row are the upper half (VC low).
        await Assert.That(frame.AnyLit(96, 99)).IsFalse();
        await Assert.That(frame.AllLit(100, 103)).IsTrue();
    }

    [Test]
    public async Task LoresColorNibbleMatchesSatherWorkedExample()
    {
        var system = new AppleIISystem();

        BootToIdle(system);

        system.WriteByteDebug(0xC050, 0); // GRAPHICS
        system.WriteByteDebug(0xC056, 0); // LORES
        system.WriteByteDebug(0xC054, 0); // PAGE1

        for (var address = 0x400; address <= 0x7FF; address++)
        {
            system.WriteByteDebug((ushort)address, 0x99);
        }

        var frame = AppleIIVideoFrame.Capture(system);

        // Sather p.8-23: nibble 1001 gives "10011001100110" on an even
        // video cycle and "01100110011001" on an odd one; adjacent cells
        // alternate.
        var even = "10011001100110";
        var odd = "01100110011001";

        for (var cell = 0; cell < AppleIIVideoFrame.Cells; cell++)
        {
            await Assert.That(frame.CellBits(50, cell)).IsEqualTo(cell % 2 == 0 ? even : odd);
        }
    }

    [Test]
    public async Task DifferentLoresColorsProduceDifferentBitStreams()
    {
        var system = new AppleIISystem();

        BootToIdle(system);

        system.WriteByteDebug(0xC050, 0); // GRAPHICS
        system.WriteByteDebug(0xC056, 0); // LORES
        system.WriteByteDebug(0xC054, 0); // PAGE1

        // Color 1 in the upper half of each text row, color 2 in the lower.
        for (var address = 0x400; address <= 0x7FF; address++)
        {
            system.WriteByteDebug((ushort)address, 0x21);
        }

        var frame = AppleIIVideoFrame.Capture(system);

        await Assert.That(frame.CellBits(96, 0)).IsNotEqualTo(frame.CellBits(100, 0));
        await Assert.That(frame.CellBits(96, 0)).IsEqualTo(frame.CellBits(97, 0));
        await Assert.That(frame.CellBits(100, 0)).IsEqualTo(frame.CellBits(103, 0));
    }

    [Test]
    public async Task Page2SwitchesLoresAddressSource()
    {
        var system = new AppleIISystem();

        BootToIdle(system);

        system.WriteByteDebug(0xC050, 0); // GRAPHICS
        system.WriteByteDebug(0xC056, 0); // LORES

        // PAGE1 memory is black (no lit bits); PAGE2 memory is white.
        for (var address = 0x400; address <= 0x7FF; address++)
        {
            system.WriteByteDebug((ushort)address, 0x00);
        }

        for (var address = 0x800; address <= 0xBFF; address++)
        {
            system.WriteByteDebug((ushort)address, 0xFF);
        }

        system.WriteByteDebug(0xC054, 0); // PAGE1
        var page1 = AppleIIVideoFrame.Capture(system);

        await Assert.That(page1.AnyLit(0, 191)).IsFalse();

        system.WriteByteDebug(0xC055, 0); // PAGE2
        var page2 = AppleIIVideoFrame.Capture(system);

        await Assert.That(page2.AllLit(0, 191)).IsTrue();
    }

    [Test]
    public async Task HiresBitZeroIsLeftmostDot()
    {
        var system = new AppleIISystem();

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

        var frame = AppleIIVideoFrame.Capture(system);

        var expectedLit = new[] { true, false, true, false, true, false, true };

        // Line 100 is comfortably inside the visible picture, away from
        // any HBL/VBL edge effects.
        for (var cell = 0; cell < AppleIIVideoFrame.Cells; cell++)
        {
            for (var dot = 0; dot < 7; dot++)
            {
                await Assert.That(frame.Dot(100, cell, dot)).IsEqualTo(expectedLit[dot]);
            }
        }
    }

    [Test]
    public async Task MixModeShowsTextForBottomFourRows()
    {
        var system = new AppleIISystem();

        BootToIdle(system);

        system.WriteByteDebug(0xC050, 0); // GRAPHICS
        system.WriteByteDebug(0xC053, 0); // MIX
        system.WriteByteDebug(0xC056, 0); // LORES
        system.WriteByteDebug(0xC054, 0); // PAGE1

        // TEXT and LORES share the same memory, so this single fill feeds
        // both interpretations: as solid white LORES in the top 160 lines,
        // and as the glyph for $FF in the bottom four text rows - which
        // can't be solid, since the character ROM leaves a blank spacer
        // column on each side of every cell.
        for (var address = 0x400; address <= 0x7FF; address++)
        {
            system.WriteByteDebug((ushort)address, 0xFF);
        }

        var frame = AppleIIVideoFrame.Capture(system);

        // Sather p.5-14: scan lines 160-191 are the bottom four text rows.
        await Assert.That(frame.AllLit(0, 159)).IsTrue();

        for (var line = 160; line < 192; line++)
        {
            await Assert.That(frame.AllLit(line, line)).IsFalse();
        }
    }

    [Test]
    public async Task VideoDataBitIsForcedLowDuringBlanking()
    {
        // Matches Gayler's "A9" blanking-gated video-data selector - no
        // mode setup needed, HBL occurs every line regardless of mode.
        var system = new AppleIISystem();

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
        var system = new AppleIISystem();

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

                // TEXT/HIRES shift once per dot (7M - once every 2 master
                // ticks), so each dot's bit occupies both of its tick slots
                // in the now per-master-tick _videoDataBits array - see that
                // field's remarks.
                for (var dot = 0; dot < 7; dot++)
                {
                    await Assert.That(bits[dot * 2]).IsEqualTo(expectedLit[dot]);
                    await Assert.That(bits[dot * 2 + 1]).IsEqualTo(expectedLit[dot]);
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
        // Sather p.8-23 ("LORES Graphics Output"): LORES's real VIDEO DATA
        // line is the active nibble loaded into a 4-bit "end around" shift
        // register clocked directly by 14M (master clock) - not once per
        // dot like TEXT/HIRES - so it circulates Q0->Q1->Q2->Q3->Q0... once
        // every 4 master ticks, 3.5 times across a 14-tick video cycle.
        // Which bit starts the rotation depends on address parity: Q0 for
        // an even memory address (H0 latched low), Q2 for odd (H0 latched
        // high) - confirmed against Sather's own worked example (nibble
        // 1001: even cycle "10011001100110" starting at Q0, odd cycle
        // "01100110011001" starting at Q2).
        var system = new AppleIISystem();

        BootToIdle(system);

        system.WriteByteDebug(0xC050, 0); // GRAPHICS
        system.WriteByteDebug(0xC056, 0); // LORES
        system.WriteByteDebug(0xC054, 0); // PAGE1

        // Same nibble in both halves of the byte, so the sampled cell's
        // expected pattern doesn't depend on which half (VC) is active.
        const byte nibble = 0b1001;
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
                var h0 = (h & 1) != 0;
                var startBit = h0 ? 2 : 0;

                var bits = system.GetVideoDataBitsForTests();

                for (var tick = 0; tick < 14; tick++)
                {
                    var bitIndex = (tick + startBit) & 3;
                    var expected = ((nibble >> bitIndex) & 1) != 0;
                    await Assert.That(bits[tick]).IsEqualTo(expected);
                }

                sampled = true;
            }

            wasPhase0 = isPhase0;
        }

        await Assert.That(sampled).IsTrue();
    }
}
