using System.Globalization;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using Aemula.Emulation.Systems.Nes.Ppu;
using FlawlessChips;

namespace Aemula.Tests.Emulation.Systems.Nes;

internal class Ricoh2C02Tests
{
    private static readonly NodeBus<ushort> pclk = new(
    [
        Flawless2C02.NodeIds.pclk0,
        Flawless2C02.NodeIds.pclk1,
    ]);

    //[Test]
    public async Task TestPpuDump()
    {
        var ppuDumpLines = File.ReadAllLines("Assets/tracelog.txt");

        var dataY = new double[0x0567 * 2];

        // Columns are:
        // cycle,hpos,vpos,ab,db,cpu_a,cpu_x,cpu_y,cpu_db,io_rw,io_ce,pclk,vid_burst_h,vid_burst_l,vid_emph,vid_luma0_h,vid_luma0_l,vid_luma1_h,vid_luma1_l,vid_luma2_h,vid_luma2_l,vid_luma3_h,vid_luma3_l,vid_sync_h,vid_sync_l

        var ppu = new Ricoh2C02(initialClock: PinValue.Low, initialPixelClock: true);

        var clk = true;

        for (var halfCycle = 0; halfCycle < ppuDumpLines.Length; halfCycle++)
        {
            ppu.Clk.Set(clk ? PinValue.High : PinValue.Low);

            var expectedLine = ppuDumpLines[halfCycle];
            var expectedLineEntries = expectedLine.Split(',');

            var cycle = halfCycle / 2;
            var messageSuffix = $"mismatch at cycle 0x{cycle:X4}";

            await Assert
                .That(cycle)
                .IsEqualTo(int.Parse(expectedLineEntries[0], NumberStyles.HexNumber))
                .Because($"cycle {messageSuffix}");

            async Task Check(int index, int value, string name)
            {
                await Assert
                    .That(value)
                    .IsEqualTo(int.Parse(expectedLineEntries[index], NumberStyles.HexNumber))
                    .Because($"{name} {messageSuffix}");
            }

            await Check(1, ppu.HPos, "hpos");
            await Check(2, ppu.VPos, "vpos");

            await Check(12, ppu.PixelClock ? 2 : 1, "pclk");

            await Check(13, ppu.VidBurstH ? 1 : 0, "vid_burst_h");
            await Check(14, ppu.VidBurstL ? 1 : 0, "vid_burst_l");

            await Check(15, 0, "vid_emph");

            await Check(16, 0, "vid_luma0_h");
            await Check(17, 0, "vid_luma0_l");
            await Check(18, 0, "vid_luma1_h");
            await Check(19, 0, "vid_luma1_l");
            await Check(20, 0, "vid_luma2_h");
            await Check(21, 0, "vid_luma2_l");
            await Check(22, ppu.VidLuma3H ? 1 : 0, "vid_luma3_h");
            await Check(23, 0, "vid_luma3_l");

            await Check(24, ppu.VidSyncH ? 1 : 0, "vid_sync_h");
            await Check(25, ppu.VidSyncL ? 1 : 0, "vid_sync_l");

            if (halfCycle < dataY.Length)
            {
                dataY[halfCycle] = ppu.VOut;
            }

            clk = !clk;
        }

        var myPlot = new ScottPlot.Plot(4000, 600);
        myPlot.AddSignal(dataY);
        myPlot.SaveFig("signal.png");
    }

    [Test]
    public async Task TestPpuDump2()
    {
        var flawless2C02 = new Flawless2C02();
        var ppu = new Ricoh2C02(initialClock: PinValue.Low, initialPixelClock: true);

        var halfCycle = 0;

        async Task VerifyState()
        {
            var messageSuffix = $"mismatch at halfCycle 0x{halfCycle:X4}";

            async Task Check(ushort nodeId, int value, string name)
            {
                await Assert
                    .That(value)
                    .IsEqualTo(flawless2C02.IsHigh(nodeId) ? 1 : 0)
                    .Because($"{name} {messageSuffix}");
            }

            async Task CheckBus<T>(NodeBus<T> bus, int value, string name)
                where T : INumberBase<T>, IUnsignedNumber<T>, IShiftOperators<T, int, T>, IModulusOperators<T, T, T>
            {
                await Assert
                    .That(value)
                    .IsEqualTo(int.CreateChecked(flawless2C02.GetBus(bus)))
                    .Because($"{name} {messageSuffix}");
            }

            await CheckBus(Flawless2C02.NodeIds.hpos, ppu.HPos, "hpos");
            await CheckBus(Flawless2C02.NodeIds.vpos, ppu.VPos, "vpos");

            await Check(Flawless2C02.NodeIds.vid_sync_h, ppu.VidSyncH ? 1 : 0, "vid_sync_h");
            await Check(Flawless2C02.NodeIds.vid_sync_l, ppu.VidSyncL ? 1 : 0, "vid_sync_l");

            await Check(Flawless2C02.NodeIds.vid_burst_h, ppu.VidBurstH ? 1 : 0, "vid_burst_h");
            await Check(Flawless2C02.NodeIds.vid_burst_l, ppu.VidBurstL ? 1 : 0, "vid_burst_l");

            await Check(Flawless2C02.NodeIds.vid_emph, 0, "vid_emph");

            await Check(Flawless2C02.NodeIds.vid_luma0_h, ppu.VidLuma0H ? 1 : 0, "vid_luma0_h");
            await Check(Flawless2C02.NodeIds.vid_luma0_l, ppu.VidLuma0L ? 1 : 0, "vid_luma0_l");
            await Check(Flawless2C02.NodeIds.vid_luma1_h, ppu.VidLuma1H ? 1 : 0, "vid_luma1_h");
            await Check(Flawless2C02.NodeIds.vid_luma1_l, ppu.VidLuma1L ? 1 : 0, "vid_luma1_l");
            await Check(Flawless2C02.NodeIds.vid_luma2_h, ppu.VidLuma2H ? 1 : 0, "vid_luma2_h");
            await Check(Flawless2C02.NodeIds.vid_luma2_l, ppu.VidLuma2L ? 1 : 0, "vid_luma2_l");
            await Check(Flawless2C02.NodeIds.vid_luma3_h, ppu.VidLuma3H ? 1 : 0, "vid_luma3_h");
            await Check(Flawless2C02.NodeIds.vid_luma3_l, ppu.VidLuma3L ? 1 : 0, "vid_luma3_l");

            //CheckBus(pclk, ppu.PixelClock ? 2 : 1, "pclk");
            await CheckBus(pclk, 0, "pclk");
        }

        async Task SetPin<TPin>(ushort nodeId, TPin pin, PinValue value)
            where TPin : IWriteablePin
        {
            flawless2C02.SetNode(nodeId, value == PinValue.High ? NodeValue.PulledHigh : NodeValue.PulledLow);

            pin.Set(value);

            await VerifyState();
        }

        // Assert RESET and initialize other inputs
        await SetPin(Flawless2C02.NodeIds.res, ppu.Res, PinValue.Low);
        await SetPin(Flawless2C02.NodeIds.clk0, ppu.Clk, PinValue.Low);
        await SetPin(Flawless2C02.NodeIds.io_ce, ppu.Dbe, PinValue.High);
        flawless2C02.SetHigh(Flawless2C02.NodeIds.@int);

        // Recalculate all nodes until the chip stabilizes
        flawless2C02.StabilizeChip();

        // Run for 4 cycles so that RESET fully takes effect
        for (var i = 0; i < 4; i++)
        {
            await SetPin(Flawless2C02.NodeIds.clk0, ppu.Clk, PinValue.High);
            await SetPin(Flawless2C02.NodeIds.clk0, ppu.Clk, PinValue.Low);
        }

        // Deassert RESET so the chip can continue running normally
        await SetPin(Flawless2C02.NodeIds.res, ppu.Res, PinValue.High);

        // Set background color.
        flawless2C02.PaletteWrite(0x00, 0x21);
        ppu.SetPaletteMemory(0x00, 0x21);

        var clk = true;

        for (halfCycle = 0; halfCycle < 2000; halfCycle++)
        {
            // Pulse clock.
            await SetPin(Flawless2C02.NodeIds.clk0, ppu.Clk, clk ? PinValue.High : PinValue.Low);

            clk = !clk;
        }
    }
}
