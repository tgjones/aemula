using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Aemula.Emulation.Chips.Ricoh2C02;
using FlawlessChips;
using static FlawlessChips.Flawless2C02.NodeIds;

namespace Aemula.Tests.Emulation.Systems.Nes;

// Bit-exact anchor for the behavioural composite-video state machine in
// Ricoh2C02Chip.Video.cs. Drives the behavioural chip and the transistor-level
// FlawlessChips.Flawless2C02 in lockstep and asserts the behavioural video-tap
// outputs (vid_sync_{h,l}, vid_burst_{h,l}, vid_luma{0..3}_{h,l}) match
// Flawless2C02 node-for-node each 12x-f_SC cell, over sync, colour burst,
// active-video luma/chroma and the vertical-blank pulses.
//
// Clock alignment: Flawless2C02's clk0 is the master clock; one clk0 edge = one
// 12x-f_SC cell. One behavioural Ricoh2C02Chip master-clock pulse (Clk low then
// high) = one master period = two cells; a PPU dot = 8 cells. The behavioural
// region set by UpdateVideoSignal for dot D lines up with Flawless cells
// [hpos D, pclk1 half .. hpos D+1, pclk0 half] - so every video-region edge
// lands on the mid-dot (pclk0 -> pclk1) boundary, and the lockstep starts on
// that boundary of hpos 1.
internal class Ricoh2C02Tests
{
    // Cells to discard after SetState() to reach the mid-dot boundary of hpos 1
    // (see the pclk0/pclk1 walk in the trace analysis).
    private const int PrimingCells = 11;

    // The behavioural dot counter is one increment ahead of Flawless's hpos while
    // a dot's cells play out, and Flawless's hpos trails the behavioural region
    // edge by up to one dot - so the flat (scanline, dot) position may lead
    // Flawless's (vpos, hpos) by 0 or 1.
    private const int TotalDots = 262 * 341;

    // The 2C02 square-wave generator takes a few cells to settle after a tap
    // column turns on (burst ~2 cells, the luma DAC ~13 at picture turn-on);
    // Flawless shows that transient, the piecewise-constant behavioural model
    // does not. Outside this guard every node is compared; inside it only the
    // sync/blanking level is (those edges are glitch-free).
    private const int PhaseSettleCells = 14;

    private readonly record struct Taps(
        bool SyncH, bool SyncL,
        bool BurstH, bool BurstL,
        bool Luma0H, bool Luma0L,
        bool Luma1H, bool Luma1L,
        bool Luma2H, bool Luma2L,
        bool Luma3H, bool Luma3L,
        ushort HPos, ushort VPos);

    // One behavioural master-clock period: Clk low then high, two 12x-f_SC cells.
    // Replaces the old chip.Tick() entry point.
    private static void Master(Ricoh2C02Chip c)
    {
        c.Clk = false;
        c.Clk = true;
    }

    private static Taps ReadFlawless(Flawless2C02 f) => new(
        SyncH: f.IsHigh(vid_sync_h), SyncL: f.IsHigh(vid_sync_l),
        BurstH: f.IsHigh(vid_burst_h), BurstL: f.IsHigh(vid_burst_l),
        Luma0H: f.IsHigh(vid_luma0_h), Luma0L: f.IsHigh(vid_luma0_l),
        Luma1H: f.IsHigh(vid_luma1_h), Luma1L: f.IsHigh(vid_luma1_l),
        Luma2H: f.IsHigh(vid_luma2_h), Luma2L: f.IsHigh(vid_luma2_l),
        Luma3H: f.IsHigh(vid_luma3_h), Luma3L: f.IsHigh(vid_luma3_l),
        HPos: f.GetBus(hpos), VPos: f.GetBus(vpos));

    private static Taps ReadBehavioural(Ricoh2C02Chip.VideoTaps t, ulong scanline, ulong dot) => new(
        t.SyncH, t.SyncL, t.BurstH, t.BurstL,
        t.Luma0H, t.Luma0L, t.Luma1H, t.Luma1L,
        t.Luma2H, t.Luma2L, t.Luma3H, t.Luma3L,
        HPos: (ushort)dot, VPos: (ushort)scanline);

    // Which tap column a Flawless sample has active (0 none, 1 sync, 2 burst,
    // 3..6 luma0..3), used only for the phase-settle guard.
    private static int Column(in Taps t)
    {
        if (t.SyncH || t.SyncL) return 1;
        if (t.BurstH || t.BurstL) return 2;
        if (t.Luma0H || t.Luma0L) return 3;
        if (t.Luma1H || t.Luma1L) return 4;
        if (t.Luma2H || t.Luma2L) return 5;
        if (t.Luma3H || t.Luma3L) return 6;
        return 0;
    }

    private static Flawless2C02 NewFlawless(string presetState, byte palette0)
    {
        var f = new Flawless2C02();
        f.SetState(presetState);
        f.RecalcNodeList();
        f.PaletteWrite(0x00, palette0);
        return f;
    }

    // Captures Flawless's per-cell tap state for cellCount cells, starting one
    // mid-dot boundary past the preset state.
    private static Taps[] CaptureFlawless(string presetState, byte palette0, int cellCount)
    {
        var f = NewFlawless(presetState, palette0);

        var clk = f.IsHigh(clk0);
        void Pulse()
        {
            clk = !clk;
            f.SetNode(clk0, clk ? NodeValue.PulledHigh : NodeValue.PulledLow);
        }

        for (var i = 0; i < PrimingCells; i++)
        {
            Pulse();
        }

        var samples = new Taps[cellCount];
        for (var i = 0; i < cellCount; i++)
        {
            Pulse();
            samples[i] = ReadFlawless(f);
        }
        return samples;
    }

    // Runs the behavioural chip for the captured window and asserts each cell.
    // startScanline/startDot are the Flawless (vpos, hpos) the capture began on.
    private static async Task CompareAsync(
        Taps[] expected, byte palette0, ulong startScanline, int chromaSeed, string label)
    {
        var chip = new Ricoh2C02Chip();
        chip.SetPaletteMemory(0x00, palette0);
        // Capture began on hpos 1; the behavioural region for that dot is set on
        // the master-clock pulse that opens the lockstep, so seed the dot counter to 1.
        chip.SeedVideoState(startScanline, dot: 1, chromaPhase: chromaSeed);

        var prevColumn = -1;
        var guardUntil = -1;

        for (var i = 0; i < expected.Length; i++)
        {
            if (i % 2 == 0)
            {
                Master(chip);
            }

            chip.NextVideoCell();
            var actual = ReadBehavioural(chip.SampleVideoTaps(), chip.CurrentScanline, chip.CurrentDot);
            var want = expected[i];

            // 1. Position must not drift: behavioural (scanline, dot) leads
            //    Flawless (vpos, hpos) by 0 or 1 dot, always.
            var flatBehavioural = (long)actual.VPos * 341 + actual.HPos;
            var flatFlawless = (long)want.VPos * 341 + want.HPos;
            var lead = ((flatBehavioural - flatFlawless) % TotalDots + TotalDots) % TotalDots;
            if (lead > 1)
            {
                await Assert.That(lead).IsLessThanOrEqualTo(1L)
                    .Because($"{label}: position drift at cell {i}: behavioural " +
                        $"(sl {actual.VPos}, dot {actual.HPos}) vs Flawless " +
                        $"(vpos {want.VPos}, hpos {want.HPos})");
            }

            // 2. The sync tip (vid_sync_l - the horizontal-sync pulse the TV
            //    locks to) is glitch-free on every line: assert it every cell.
            if (actual.SyncL != want.SyncL)
            {
                await Assert.That(actual.SyncL).IsEqualTo(want.SyncL)
                    .Because($"{label}: sync-tip mismatch at cell {i} " +
                        $"(hpos {want.HPos}, vpos {want.VPos})");
            }

            // 3. Full node-for-node match, outside the square-wave settle guard.
            //    A tap column turning on takes a few cells to settle in the real
            //    2C02 (the blanking tap included - it lags one dot at picture
            //    turn-on), so hold off for PhaseSettleCells after any column
            //    change. Column stays 1 across the front-porch/sync/back-porch
            //    edges, so those stay asserted every cell via step 2.
            var column = Column(want);
            if (column != prevColumn)
            {
                prevColumn = column;
                guardUntil = i + PhaseSettleCells;
            }

            if (i >= guardUntil)
            {
                var a = (actual.SyncH, actual.BurstH, actual.BurstL,
                    actual.Luma0H, actual.Luma0L, actual.Luma1H, actual.Luma1L,
                    actual.Luma2H, actual.Luma2L, actual.Luma3H, actual.Luma3L);
                var w = (want.SyncH, want.BurstH, want.BurstL,
                    want.Luma0H, want.Luma0L, want.Luma1H, want.Luma1L,
                    want.Luma2H, want.Luma2L, want.Luma3H, want.Luma3L);
                if (!a.Equals(w))
                {
                    await Assert.That(a).IsEqualTo(w)
                        .Because($"{label}: tap mismatch at cell {i} " +
                            $"(hpos {want.HPos}, vpos {want.VPos}), palette ${palette0:X2}");
                }
            }
        }
    }

    // Finds the free-running chroma-phase seed that locks the behavioural burst
    // square wave onto Flawless. Calibrated once here so the arithmetic is
    // checked rather than trusted; every case then reuses it.
    private static async Task<int> CalibrateChromaSeedAsync(Taps[] expected, byte palette0, ulong startScanline)
    {
        // Only the first couple of scanlines are needed to lock the phase, and
        // they must be regions the behavioural model reproduces exactly (active
        // picture + burst) - not the vertical-sync serration.
        var window = Math.Min(expected.Length, 2 * 341 * 8);

        var matches = new List<int>();
        for (var seed = 0; seed < 12; seed++)
        {
            var chip = new Ricoh2C02Chip();
            chip.SetPaletteMemory(0x00, palette0);
            chip.SeedVideoState(startScanline, dot: 1, chromaPhase: seed);

            var ok = true;
            var prevColumn = -1;
            var guardUntil = -1;
            for (var i = 0; i < window && ok; i++)
            {
                if (i % 2 == 0)
                {
                    Master(chip);
                }
                chip.NextVideoCell();
                var t = chip.SampleVideoTaps();
                var want = expected[i];

                var column = Column(want);
                if (column != prevColumn)
                {
                    prevColumn = column;
                    guardUntil = (column >= 2) ? i + PhaseSettleCells : i;
                }
                if (i < guardUntil)
                {
                    continue;
                }

                var a = (t.BurstH, t.BurstL, t.Luma0H, t.Luma0L, t.Luma1H, t.Luma1L,
                    t.Luma2H, t.Luma2L, t.Luma3H, t.Luma3L);
                var w = (want.BurstH, want.BurstL, want.Luma0H, want.Luma0L, want.Luma1H, want.Luma1L,
                    want.Luma2H, want.Luma2L, want.Luma3H, want.Luma3L);
                if (!a.Equals(w))
                {
                    ok = false;
                }
            }
            if (ok)
            {
                matches.Add(seed);
            }
        }

        await Assert.That(matches.Count).IsEqualTo(1)
            .Because($"exactly one chroma-phase seed should lock; got [{string.Join(",", matches)}]");
        return matches[0];
    }

    [Test]
    public async Task VideoTapsMatchFlawless2C02_ActivePictureBurstAndSync()
    {
        // Representative palette[0] values: luma codes, hues, and specials.
        byte[] palettes =
        {
            0x00, 0x10, 0x20, 0x30, // greys ($x0 - constant _h)
            0x01, 0x06, 0x0C,       // hues at luma 0
            0x21, 0x16, 0x2C,       // hues across luma codes
            0x0D, 0x1D,             // $xD - constant _l
            0x0F,                   // $xF - blanking
        };

        // From the pre-render line through four visible lines: covers blank head,
        // burst, active-video luma/chroma, and the 3-scanline chroma repeat.
        const int cells = 5 * 341 * 8;
        const ulong startScanline = 261;

        var calibration = CaptureFlawless(
            Flawless2C02.PresetStates.PreRenderEven, 0x21, cells);
        var seed = await CalibrateChromaSeedAsync(calibration, 0x21, startScanline);
        await Assert.That(seed).IsEqualTo(1).Because("expected calibrated chroma seed");

        foreach (var palette0 in palettes)
        {
            var expected = CaptureFlawless(
                Flawless2C02.PresetStates.PreRenderEven, palette0, cells);
            await CompareAsync(expected, palette0, startScanline, seed,
                $"active/burst palette ${palette0:X2}");
        }
    }

    [Test]
    public async Task VideoTapsMatchFlawless2C02_VerticalBlank()
    {
        // From the post-render line through the vertical-sync pulses and back to
        // blank lines: post-render 240, blank lines 242-243/248+, broad serrated
        // vsync 244-247.
        const int cells = 13 * 341 * 8;
        const ulong startScanline = 240;
        const byte palette0 = 0x21;

        var expected = CaptureFlawless(
            Flawless2C02.PresetStates.PostRenderOdd, palette0, cells);
        var seed = await CalibrateChromaSeedAsync(expected, palette0, startScanline);

        await CompareAsync(expected, palette0, startScanline, seed, "vblank");
    }
}
