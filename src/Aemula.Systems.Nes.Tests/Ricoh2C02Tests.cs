using System.Globalization;
using System.IO;
using Aemula.Systems.Nes.Ppu;
using Aemula.Systems.Nes.Tests.Visual2C02;
using NUnit.Framework;

namespace Aemula.Systems.Nes.Tests;

internal class Ricoh2C02Tests
{
    [Test]
    public void TestVisual2C02()
    {
        var macros = new Macros();

        var cpuCommands = new CpuCommands(macros);
        cpuCommands.Next();

        while (true)
        {
            cpuCommands.Next();

            macros.Step();

            if (macros.ReadBits<ushort>(NodeGroups.vpos) == 240)
            {
                break;
            }
        }

        //var expectedState = Configuration.ResetStatePostRenderOdd;
        var expectedState = "xvgllhlllllhhhxxxhhlllllxlllllhhxxxhhlxhlllllllllllhhhlllhlxhhlhlllhlhlllllllhxlhxxhxllhhxxxxhlhxhxlhxxxxlhlxxxlxllhlxhlhlxhxhlxhxxxxxxlxxxxxxxxxxxxxhhxhhxhllxxxxxlxxxxhxlhlxxxxxxhhxxxxxxxxxlhxllhhhllxxxhhhxxxllxhxlxxxhlllhhlhhllhhhxxllxlxhhhxhhlxhhxhxxhhhxxxlllxxxxxxxxxxhhhxxlllhhlhxlxxlxxhxxhxxhxxlhxhhxlhlhllxhxxlllllllllllllxxlxlhlhxxxxhxxlxhxxxhxxxxhhlxlxxhxhlxhhxxxxhxxlllxhlllxxxhxhllxxxxllhxxxxxxhxxxxxxxxhhxxxlxlhlxxlhlhlhhlhlhllhllhllllhxlllhlllxxhllllhxxxlxxxxxhhlhhhxxlhlhhxhlhlhxxxlhxhhhxxxhlllllhlxxxhhxhxxxllhxllxxlxxlxxhlhxxxlhlhhllxxxllxxxxlhhxxxxhlxxxxxhhlhxxxxxxxhlxxxhlxxxxxxxllhxxlhlxxxlxxlxllxlhlhllhlhhlhhlhllxxxxlllxxxhhxxxxxxhxxxhhxxxxlxhhlhxxhxhxxxxxxxhxlxxxxlhhhllhxhhxxxxxxxxxxhxhxxhxxhxxhllxxxxlxxxhxhhhxxlxxxxxxxlxlhxxxxhhxxxxxhxxxxxxhxxxxxhhhhxxxxxxhxxxxxlxlhxxxxxxxxxxxlhxxxhxhhhhxlxlhlxlxlllxhlllhhlllllllllllllllllllhlllhlxxlxxlllhhxlhllhhhhhhhhhhhxxxxxxxxlhhlhhlxxxxllhllllllhhxhhhhhhxllxxxxxxhxhxhxhxhlhlhhxlllxhhhlhllxhxxxhxxxxxxlxlxhhxxllllhhhhxlxhxllxhlxlxlxxxhhllxxhlxlxxxlhhxxxxxxxxxxlxxxhhxxlxllllxxhxxxxlxxxhllxxhhlxxxxxlxxxxlllxxxxxlxlxlxxlhxxxxllxlxxlxhhlxhxxhxlxlxlhhhhxhxxxhxxhlllhlxhhhxxhxhxllllxxlllxxhxlhxllxhxllxxlhlhxlxhhhxxhxlxxhxhhxxxhxlxhhxxxxxhhxlxxxhhhxhxxxllhxxhxxxlhxxxxxxxxhlxxhxxlxlxlxxlhxhxhxxxhhxxhxhxlllxhlxxxhlhlxhlllhhxxhxhxhxhxhxhxxxxlxhlxxxxlhxxxhxxxxllhxxxxhlhlxhxhxlhlxhhhlxxxxhhlxxxhlhhxlxhxxxxxxxlhlhhhhhhlxxxxhhlhxlxxlxhlxxhlxxlxxxhhhlhhhlhhhhhhllhhhlhlhlhhlhlhlhhlhlhlhlhlhlhhhlhlhlhlhhxllhxxxxxxxxxhxxlxxxxxhxxxlhhxxhlxxxxhhhxhxxhhxxhxxlhlxxhxllxlxllxhlxhxhhxhxhlxhlhxxxhxxxhlhxxxlhllxxxhhlxxxhlhxllhhhhhxlhxxlxxhlhxxhhxxlxlhxhhlllxxxxllxxxhxxxxxxxxxhxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxhxxxxxlxxxxlhxxlhxhhxxxxlxxxxxlxxxlxxxlxxxlxxlxxlxlxxxhxlhllhxxxlxlhxxxxlxxxhlxhxxxxxhlxxxxxxhxxxxxxhxxxxxxxxxxxlxxxlllllxlhlxxxlllllxlhxxlhllxhxxxxxxlxxxxlxhllllhllllhllhlllhhlllxhlxhlxxllxxlhxlllxlxxhhxxhxxlxhhhhhhhhhhhhhhhhhxxxxhhhhhhhhhhhhhhhhxhxxxxxxxhhhhhhhhhhhhhhhhhxxxxxlhhhhhhhhhhhhhhxhhlxlxxlxxhxxhhhhhhhhhhhhhhhhhxxhxxlhxhxhhhhhhhhhhhhhxhhhhhxlhxhxhlxxxhhhhhhhhhhhhhhhhlhxhxhllxxxxxxxxxxxxxxlllxxxxxlxhxlxhxxxxxxxxhllllhlxlhlhlhlxhlhxlhlhlhlhlhllhllhhxhlllhxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxllllllllxxxxxxxxhhhhhhhlxxxxxxxxxllllllllxxxxxlxxlxxlxxlxxhxllxxlxxxxxxxxxxxxxxxxxxhhhhhxxxxhhhhhhhlllxxlllllxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxllxxxllxlxxlxlhhxxxxlllllllllllllllxhlxxxxxxxxxlxhllllllllllxllxlxxlllxllllllllxxllllllllxhhhhhhhhllllllllhxhhhhhhhhhxllllllllhhhhhhhhxhhhhhhhhxhhhhhhhhxhhhhhhhhhxxxxxxxxxxllllllllxxllllllllxxlxxxxxxxxxllllllllllllllllllllxxxxxxxxxlxllllllllxxxxxxxxxxxxllllllllxllllllllxllllllllxxhxlxllxxxxxxxhlxxlhllllllllxlllxlllllxxhhxxxllxxxxxxxxxhllllllllllxhxllllllllllllxxxxxxxxxllllllllxxllllllllxxxxhxxxxxxxxlllllllllhxhxhxhhhhxxxxxxxxxxxxxxxxhxxxxxxxxxxxxxxxxxllllllllllxlxxxlxxxxxxxxhhhllllllllllxxxxxxllllllllllllxlllxxlxllllllxxxxxxhhhhhlllxxlllhllllllllllllxxxxxxxxxxxxllllllhlllhhlhhhlllllllhhhhlxhlxlhxlhxlhhlxllhhlllhlxhxxlhllxlhlxllxxxxxxxxxllxxhllllllllllllllllllllllllllllllllxhhhhlhllllhhlhllllhlllllllhllhhlllhlhhhllllllllllllllllllllllllllllllllllllhhhlllllllllllllllllhlhllllhhlhllllhlllllllhllhhlllhxxxxllllllllllllllllllllllllllllllllxxxxxxlhhhhhhhllhllllhhlhllllhlllllllhllhhlllhlhlhhhhlllllllllllllllllllllllllllllllxllhhlxhlllhxllhlllllhlhllllhlllllllhllhhlllhhhllllllhllllllllllllllllllllllllxxlxxxxxxxxxlhlhllllhhlhllllhlllllllhllhhlllhhxlllllllllhllllllllllllllllllllllllllllllllllhhlllhhhhhhhhhhxxxlllhhlhllllhhlhllllhlllllllhllhhlllhllllllllllllllllllllllllllllllllxxlllllllllxhllhllllhhlhllllhlllllllhllhhlllhxxxxxxxxhlllllllllllllllllllllllllllllllxlllhlhhhxlxlhllhllllhhlhllllhlllllllhllhhlllhllhlllllllllllllllllllllllllllllllxxxxxlhlhllllhhlhllllhlllllllhllhhlllhlxlxllllllllllllllllllllllllllllllllllxxxllllhlxlhxhhlhhlhllllhhlhllllhlllllllhllhhlllhlhllllllllllllllllllllllllllllllllxxxhllhhhhhxhlhlllllhlhllllhllllllllllhhlllhxxxxxxxlllllllhlllllllllllllllhlllllllllllhhhllllhllllhhlhllllhlllllllhllhhlllhxhllllllllllllllllllllllllllllllllhllxxxxxxhhxxhllllhllllhhlhlllllllllllllllhhlllhhhllllllllllllllhlllllllhllllllllhhllhlhlhhhhhlhllllhhlhllllhlllllllhllhhlllhxlllllllllllllllllllllllllllllllllllxlhhhxhlxxllhhhhlllhhlhllllhhlhllllhlllllllhllhhlllhhllllllllllllllllllllllllllllllllhllhllhhllhllllhhlhllllhlllllllhllhhlllhxxxxxxlxhllllllllllllllllllllllllllllllllxxxxxllxxlhxxxhhlllhhllhllllhhlhllllhlllllllhllhhlllhxhllllllllllllllllllllllllllllllllxlxxlxllhhlllhlhllllhhlhllllhlllllllhllhhlllhhlllllllllllllllllllllllllllllllllhllllhhlhllllhhlhllllhlllllllhllhhlllhlhhxllllllllllllllllllllllllllllllllllllhxlllhhlhhhhlhlllllhlhllllllllllllhllhhlllhxlllllllhlllllllhllllllllllllllllllllxhhhhlllllhllllhhlhllllhlllllllhllhhlllhhlllllllllllllllllllllllllllllllxxlxxlllhllllhllllhhlhllllhllllllllllhhlllllhllllllllllllllllllllllhlllllllhxxxxxxxxxxxxxxlhlhllllhhlhllllhlllllllhllhhlllhxlllllllllllllllllllllllllllllllllxxhlhllllhhlhllllhlllllllhllhhlllhlxxxxxxxxxxxxxxxxxllllllllllllllllllllllllllllllllllllhllllhhlhllllhlllllllhllhhlllhhlllllllllllllllllllllllllllllllhllhllllhhlhllllhlllllllhllhhlllhhlhlllllllllllllllllllllllllllllllxlxxxxxhlhlhllllhhlhllllhlllllllhllhhlllhllllllllllllllllllllllllllllllllxxxxhllhhllhlhlhllllhhlhllllhlllllllhllhhlllhllllllllllllllllllllllllllllllllhhllhhlhlllllhlhlllllllllllllllhhlllllllllllhlllllllhlllllllhlllllllhhhhhlllxllhllllhhlhllllhlllllllhllhhlllhhhhlllllllllllllllllllllllllllllllxlhhhhhxxxlxhxhlhhhllhhlhllllhhlhllllhlllllllhllhhlllhhllhlllllllllllllllllllllllllllllllllllhxlhhhlllhllllhhlhllllhlllllllhllhhlllhhlllllllllllllllllllllllllllllllxlxxxxllhhlxlxxxxxxxxxxxxlllhllllhhlhllllhlllllllhllhhlllhlhlllllllllllllllllllllllllllllllxxlxllhlhllllhhlhllllhlllllllhllhhlllhxxxxlllllllllllllllllllllllllllllllllhlhlllllllllllllhlllllllllxlllllhlhllllllllllllllllllllllllllllllllhhhhllllhlhhhllllhhhllxllhhlhhhxhlhhhhhhllxlhlhlxhhxlhhhhllhxhhhhhllhxllxxxlhhlhlhhhlhhhhlhhhlhllhhlhlllhlxlhlxhlhhhllhxhllxlxhllllhhhxhllxhlxlhlhxllxxlxxxhxhxhhlhxllxxlxllhlhllllhhlhllllhlllllllhllhhlllhxlllllllllllllllllllllllllllllllllxlhhlhllllhhlhllllhlllllllhllhhlllhlllllllllllhhllllllllllllllllllllllllllllllllhxhhxllhllllhhlhllllhlllllllhllhhlllhhlllllllllllllllllllllllllllllllxllxxhhhhhhhhhhhhhhhxxhlhllllhhlhllllhlllllllhllhhlllhllllllllllllllllllllllllllllllllxllllllxllllllllllllllllllllxhlxllhllllhhlhllllhlllllllhllhhlllhhlllllllllllllllllllllllllllllllhlhhllhlllllhlhllllhlllllllhllhhlllhlxhllllllhllllllllllllllllllllllllxxxxxxxxxxxxxxllhlxxlhlhllllhhlhllllhlllllllhllhhlllhxllllllllllllllllllllllllllllllllllhxhxhxlxhlhlllllhlhllllhllllllllllhhlllhlllllllhlllllllllllllllhllllllllhlllllxllllllhlhllllhhlhllllhlllllllhllhhlllhllllllllllllllllllllllllllllllllxxxxxxhhhllllllllllhllllhhlhllllhlllllllhllhhlllhllllllxhlllllllllllllllllllllllllllllllhllllllllhhlllhlhllllhhlhllllhlllllllhllhhlllhlxlllllllllllllllllllllllllllllllllhllllhhhhllhllllhhlhllllhlllllllhllhhlllhlhhhhllhlllllllllllllllllllllllllllllllhxxxxxxxlxxxxxxxxxxxxllllllhhhhllhxllhllllhhlhlllllllllllllllhhlllhllllllhllllllllllllllhlllllllhllllllllxlhhhxhhxlhhxxhlhllllhhlhllllhlllllllhllhhlllhllllllllllllllllllllllllllllllllxlhlhlllllhlhllllllllllllhllhhlllhhhhhhhhlllllllhlllllllhllllllllllllllllxxxxxxhlhllllhhlhllllhlllllllhllhhlllhxxxxxxllllllllllllllllllllllllllllllllxxxlllllhhllhllllhhlhllllhllllllllllhhllllhllllllllllllllllllllllhlllllllhlxhhlllhllllhhlhllllhlllllllhllhhlllhlhllllllllllllllllllllllllllllllllllhhlhllllhhlhllllhlllllllhllhhlllhllllllllllllllllllllllllllllllllllhxxxxxhlhllllhhlhllllhlllllllhllhhlllhlllllllllllllllllllllllllllllllllhxlllhllhllllhhlhllllhlllllllhllhhlllhxhllllllllllllllllllllllllllllllllllllxlhhhhhhhhhhhhhhhhhhhhhhhhlllllllllllllllllllllllllhhhhlhhhhhxlllhllllhhlhllllhlllllllhllhhlllhhlllllllllllllllllllllllllllllllxlhlhlhlhlhlhlhlhlhlhlhlhlhlhlhlhlhlhlhlhlhlhlhlhlhlhllllhhlhllllhlllllllhllhhlllhllllllllllllllllllllllllllllllllllllhhhlhhhhhllhlhlllllhlhlllllllllllllllhhllllllllllllhlllllllhlllllllhlllllllhllhhhhhhhhhhhhhhhhhhhhhhhhllllllllllllllllllllllllhhlhllllhhlhllllhlllllllhllhhlllhlhllllllllllllllllllllllllllllllllllllhhhhlhlhlhlhlhlhlhlhlhlhlhlhlhlhlhlhlhlhlhlhlhlhlhlhlllllhllllhhlhllllhlllllllhllhhlllhhlllllllllllllllllllllllllllllllxlllxlllhllllhhlhllllhlllllllhllhhlllhhllllllllllllllllllllllllllllllllhhhhhhhhhhhhhhhhhhhhhhhhllllllllllllllllllllllllxlhhhhxlxhlhllllhhlhllllhlllllllhllhhlllhlllllllllllllllllllllllllllllllllhlhlhlhlhlhlhlhlhlhlhlhlhlhlhlhlhlhlhlhlhlhlhlhlhlhllllhhlhllllhlllllllhllhhlllhlllllllllllllllllllllllllllllllllxlxxxxllhllllhhlhllllhlllllllhllhhlllhlhhhhhhhhhhhhhhhhhhhhhhhhllllllllllllllllllllllllhlllllllllllllllllllllllllllllllllhllllhhlhllllhlllllllhllhhlllhhlllllllllllllllllllllllllllllllhlhllllhhlhllllhlllllllhllhhlllhllllllllllllllllllllllllllllllllxxxxlxxxlhlxxxxxxxhlxhhhhhhxxxxxllllllxhlllhhhhhhhhhhhhhhhhhlhlhlhlhlhlhlhlxxxxxxxlllllhhhlllhlhlhlhlhllhhhhhlhllxxxxxxxxllllllllxxxxxxxxllllllllhhhhhhhhhhhhhhhhllllllllllllllllllhlhhhhhhhhllhlllllxxxxxxxxxxxllllllllxxxxxxxxhhllhhllhhllhhllhhhhhhhhhhlllllllllllllllllhxhhxxxxxxxxllllllllxxxxxxxxhhllhhllhhllhhllhhhhhhhhhlllllllllllllllllhxhllllllllhhhhxxxxxxxxllllllllxxxxxxxxhllhhllhhllhhllhhhhhhhhhhhlllllllllhhhhhhhhlllllhllllllllllhxxxhhxxxxxxxxlllllllllxxxxxxxxxlhllhhllhhllhhllhhhhhhhhhllllllllllllllllllllllllllllllhhllxhhhhhhhhhhhhhxxxxxxxxllllllllhxxxxxxxxhllhhllhhllhhllhhhhhhhhhlllllllllllllllllllllxlllllllllhxxllxxxxxxxxxhllllllllhhhhhhhhhhhhhxxxxxxxxhllhhllhhllhhllhhhhhhhhhlllllllllllllllllhxhxxxxxxxxxllllllllxxxxxxxxxhllhhllhhllhhllhhhhhhhhhhllllllllhllllllllllhxhlxxxxxxxxxhhhhhhhhhhlxhhhhhhhhxxxxxxxxxxxxllxxxxllllllhhhhhhhhxxxxxxxxxxxxxxxhhhhhhhhhllllllllhhhhhhhhhhhhhllhhhhllllllxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxhhxxxxxxxxxxxxxxxxxxxxxxxxxhhhhhhhhhhhhhhllllhllllxhhlllllllllllllllhhxhxxxxhhllllllllllllllhllllxhxlxxhllllllllxlllllllllllllxxxxxxxxxhxhhhhhhhhxxxxxxxxlhhhhhhhhhhhhhhhhhhxxxxxxxxllllxxxxxxxxxhxxxxxxxxxxxxxxxxxxxhhhhhhhhlllxxxxxxxxlhhhhhhhhxhhhhhhhhhxxxhhhhhhhhllllllllxllllllllllllllllllhhhhhhhhlllxxxxxxxxlhhhhhhhhxhhhhhhhhhxhhhhhhhhllllllllhhhhhhhhhhhhhhhhhhhhhhhhxxxxxxxxhhhhhhhhxllllllllhhhhhhhhhhhhhhhhllllllllhhllllllllhhhlxxxxxxxxxhhhhhhhhhxhlxxxhhhhhhhhhllhhhhhhhhlllllllllllhxhhhllllllllhhhhhhllllxxxxxxxxlllhhhhhhhhllllllhhhhhhhhhlllxhhhhhhhhhxhhhhhhhhlllllllllxhhllllllllhhhhhhhhhhhhlxhllhhhhhhhhllllllxlxhxllllxxxxxxxxlhhhhhhhhxxxxxhhhhhhhhhxhhhhhhhhlllllllllllllllllllllllllllllllllllllllllllxxxxxxxxlhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhxhhhhhhhhhxxxhhhhhhhhllllllllxxllllllllllllllllllllllllllllllllhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhxxxxxxxxhhhhhhhhllllllllllllllllhhhhhhhhlhhllhhhhhhhhhhhxlxxllhlllhhllhxllhlhxxhlhlhhhhhllhhlhhhllxhhhhhhhhhhxxxxxlllllllhhhhhhhxlllhlhhlhhllllllllllllxhllllllllllllhllllllllllllllhhlllllllhllllllllllhllhhhhhllllllllllllllllllllllxllhhhhhhhhhhhhhhlhhllllllllllllllllllllllllllllllllllllllllllllllllllllllllllllllllllllllllhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhh";
        var actualState = macros.GetState();

        Assert.AreEqual(expectedState, actualState);
    }

    //[Test]
    public void TestPpuDump()
    {
        var ppuDumpLines = File.ReadAllLines("Assets/tracelog.txt");

        var dataY = new double[0x0567 * 2];

        // Columns are:
        // cycle,hpos,vpos,ab,db,cpu_a,cpu_x,cpu_y,cpu_db,io_rw,io_ce,pclk,vid_burst_h,vid_burst_l,vid_emph,vid_luma0_h,vid_luma0_l,vid_luma1_h,vid_luma1_l,vid_luma2_h,vid_luma2_l,vid_luma3_h,vid_luma3_l,vid_sync_h,vid_sync_l

        var ppu = new Ricoh2C02(initialClock: false, initialPixelClock: true);

        var clk = true;

        for (var halfCycle = 0; halfCycle < ppuDumpLines.Length; halfCycle++)
        {
            ppu.Clk = clk;

            var expectedLine = ppuDumpLines[halfCycle];
            var expectedLineEntries = expectedLine.Split(',');

            var cycle = halfCycle / 2;
            var messageSuffix = $"mismatch at cycle 0x{cycle:X4}";

            Assert.AreEqual(int.Parse(expectedLineEntries[0], NumberStyles.HexNumber), cycle, $"cycle {messageSuffix}");

            void Check(int index, int value, string name)
            {
                Assert.AreEqual(
                    int.Parse(expectedLineEntries[index], NumberStyles.HexNumber),
                    value, 
                    "{0} {1}",
                    name,
                    messageSuffix);
            }

            Check(1, ppu.HPos, "hpos");
            Check(2, ppu.VPos, "vpos");

            Check(12, ppu.PixelClock ? 2 : 1, "pclk");

            Check(13, ppu.VidBurstH ? 1 : 0, "vid_burst_h");
            Check(14, ppu.VidBurstL ? 1 : 0, "vid_burst_l");

            Check(15, 0, "vid_emph");

            Check(16, 0, "vid_luma0_h");
            Check(17, 0, "vid_luma0_l");
            Check(18, 0, "vid_luma1_h");
            Check(19, 0, "vid_luma1_l");
            Check(20, 0, "vid_luma2_h");
            Check(21, 0, "vid_luma2_l");
            Check(22, ppu.VidLuma3H ? 1 : 0, "vid_luma3_h");
            Check(23, 0, "vid_luma3_l");

            Check(24, ppu.VidSyncH ? 1 : 0, "vid_sync_h");
            Check(25, ppu.VidSyncL ? 1 : 0, "vid_sync_l");

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

    //[Test]
    public void TestPpuDump2()
    {
        var ppuDumpLines = File.ReadAllLines("Assets/tracelog2.txt");

        var dataY = new double[0x0567 * 2];

        // Columns are:
        // 0 = cycle,hpos,vpos,vbl_flag,spr0_hit,spr_overflow,vramaddr_t,vramaddr_v,io_db,io_ab,io_rw,io_ce,rd,wr,ab,ale,db,
        // 17 = vid_sync_h,vid_sync_l,vid_burst_h,vid_burst_l,vid_emph,vid_luma0_h,vid_luma0_l,vid_luma1_h,vid_luma1_l,
        // 26 = vid_luma2_h,vid_luma2_l,vid_luma3_h,vid_luma3_l,pclk

        var ppu = new Ricoh2C02(initialClock: false, initialPixelClock: true);

        // Set background color.
        ppu.SetPaletteMemory(0x00, 0x21);

        var clk = true;

        for (var halfCycle = 0; halfCycle < ppuDumpLines.Length; halfCycle++)
        {
            ppu.Clk = clk;

            var expectedLine = ppuDumpLines[halfCycle];
            var expectedLineEntries = expectedLine.Split(',');

            var cycle = halfCycle / 2;
            var messageSuffix = $"mismatch at cycle 0x{cycle:X4}";

            Assert.AreEqual(int.Parse(expectedLineEntries[0], NumberStyles.HexNumber), cycle, $"cycle {messageSuffix}");

            void Check(int index, int value, string name)
            {
                Assert.AreEqual(
                    int.Parse(expectedLineEntries[index], NumberStyles.HexNumber),
                    value,
                    "{0} {1}",
                    name,
                    messageSuffix);
            }

            Check(1, ppu.HPos, "hpos");
            Check(2, ppu.VPos, "vpos");

            Check(17, ppu.VidSyncH ? 1 : 0, "vid_sync_h");
            Check(18, ppu.VidSyncL ? 1 : 0, "vid_sync_l");

            Check(19, ppu.VidBurstH ? 1 : 0, "vid_burst_h");
            Check(20, ppu.VidBurstL ? 1 : 0, "vid_burst_l");

            Check(21, 0, "vid_emph");

            Check(22, ppu.VidLuma0H ? 1 : 0, "vid_luma0_h");
            Check(23, ppu.VidLuma0L ? 1 : 0, "vid_luma0_l");
            Check(24, ppu.VidLuma1H ? 1 : 0, "vid_luma1_h");
            Check(25, ppu.VidLuma1L ? 1 : 0, "vid_luma1_l");
            Check(26, ppu.VidLuma2H ? 1 : 0, "vid_luma2_h");
            Check(27, ppu.VidLuma2L ? 1 : 0, "vid_luma2_l");
            Check(28, ppu.VidLuma3H ? 1 : 0, "vid_luma3_h");
            Check(29, ppu.VidLuma3L ? 1 : 0, "vid_luma3_l");

            Check(30, ppu.PixelClock ? 2 : 1, "pclk");

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
}
