using System.Collections.Generic;
using System.Threading.Tasks;
using Aemula.Emulation.Chips.Tia;

namespace Aemula.Tests.Emulation.Chips.Tia;

// Unit-level ball tests, same harness shape as TiaMissileTests: Osc pulses
// step the colour-clock/horizontal machinery, register writes go straight in
// via a CS1-selected Phi2 edge. Each positioning test strobes RESBL partway
// into a line, lets the ball counter settle over two lines, then scans a
// whole visible region reading TiaChip.Ball.PixelOn per colour clock.
//
// Absolute pixel columns depend on TIA's internal counter phase, so ball
// position is asserted differentially (delta between two configs). Colour and
// priority are checked by calling ResolveVideoOutput directly, as
// TiaCompositingTests does.
public class TiaBallTests
{
    private const byte Colup0 = 0x06;
    private const byte Colup1 = 0x07;
    private const byte Colupf = 0x08;
    private const byte Ctrlpf = 0x0A;
    private const byte Resbl = 0x14;
    private const byte Enabl = 0x1F;
    private const byte Hmbl = 0x24;
    private const byte Hmove = 0x2A;
    private const byte Hmclr = 0x2B;

    private const byte EnableD1 = 0b10;
    private const byte CtrlpfScore = 0b0000_0010;    // D1
    private const byte CtrlpfPriority = 0b0000_0100; // D2

    private static TiaChip NewTia() => new() { CS1 = true };

    private static void Write(TiaChip tia, byte address, byte data)
    {
        tia.RW = false;
        tia.Address = address;
        tia.Data05 = (byte)(data & 0x3F);
        tia.Data67 = (byte)(data >> 6);
        tia.Phi2 = false;
        tia.Phi2 = true;
    }

    private static byte ColorLuma(int hue, int luma) => (byte)((hue << 4) | (luma << 1));

    // One master OSC pulse == one colour clock.
    private static void Tick(TiaChip tia)
    {
        tia.Osc = false;
        tia.Osc = true;
    }

    private static void NextLine(TiaChip tia)
    {
        while (!tia.Blk)
        {
            Tick(tia);
        }

        while (tia.Blk)
        {
            Tick(tia);
        }
    }

    private static bool[] ScanBall(TiaChip tia)
    {
        NextLine(tia);

        var lit = new List<bool>();
        while (!tia.Blk)
        {
            lit.Add(tia.Ball.PixelOn);
            Tick(tia);
        }

        return lit.ToArray();
    }

    // Strobe RESBL atX colour clocks into a visible line, then let the ball
    // counter self-settle over two more lines so it sits at a stable column
    // near atX on the lines scanned afterwards.
    private static void PositionBall(TiaChip tia, int atX)
    {
        NextLine(tia);
        for (var i = 0; i < atX; i++)
        {
            Tick(tia);
        }

        Write(tia, Resbl, 0);

        NextLine(tia);
        NextLine(tia);
    }

    private static int FirstLit(bool[] scan)
    {
        for (var i = 0; i < scan.Length; i++)
        {
            if (scan[i])
            {
                return i;
            }
        }

        return -1;
    }

    private static int LitCount(bool[] scan)
    {
        var n = 0;
        foreach (var b in scan)
        {
            if (b)
            {
                n++;
            }
        }

        return n;
    }

    private static int LongestRun(bool[] scan)
    {
        var best = 0;
        var current = 0;
        foreach (var b in scan)
        {
            current = b ? current + 1 : 0;
            if (current > best)
            {
                best = current;
            }
        }

        return best;
    }

    [Test]
    public async Task EnablGatesTheBallPixel()
    {
        var tia = NewTia();
        Write(tia, Ctrlpf, 0x00);
        Write(tia, Hmclr, 0);
        PositionBall(tia, 40);

        Write(tia, Enabl, EnableD1);
        await Assert.That(LitCount(ScanBall(tia))).IsGreaterThan(0);

        Write(tia, Enabl, 0);
        await Assert.That(LitCount(ScanBall(tia))).IsEqualTo(0);

        // D0 must not enable it - only D1 does.
        Write(tia, Enabl, 0b01);
        await Assert.That(LitCount(ScanBall(tia))).IsEqualTo(0);
    }

    [Test]
    [Arguments(0x00, 1)]
    [Arguments(0x10, 2)]
    [Arguments(0x20, 4)]
    [Arguments(0x30, 8)]
    public async Task BallWidthFollowsCtrlpfD4D5(byte ctrlpf, int expectedWidth)
    {
        var tia = NewTia();
        Write(tia, Ctrlpf, ctrlpf);
        Write(tia, Hmclr, 0);
        Write(tia, Enabl, EnableD1);
        PositionBall(tia, 40);

        await Assert.That(LongestRun(ScanBall(tia))).IsEqualTo(expectedWidth);
    }

    [Test]
    public async Task ResblSetsTheBallColumn()
    {
        var tia = NewTia();
        Write(tia, Ctrlpf, 0x00);
        Write(tia, Hmclr, 0);
        Write(tia, Enabl, EnableD1);

        PositionBall(tia, 30);
        var near30 = FirstLit(ScanBall(tia));

        PositionBall(tia, 70);
        var near70 = FirstLit(ScanBall(tia));

        await Assert.That(near30).IsGreaterThanOrEqualTo(0);
        await Assert.That(near70).IsGreaterThanOrEqualTo(0);

        // Moving the strobe 40 colour clocks later moves the ball 40 colour
        // clocks right.
        await Assert.That(near70 - near30).IsEqualTo(40);
    }

    [Test]
    [Arguments(0x20, -2)] // HMBL = +2 -> object moves 2 colour clocks left
    [Arguments(0xE0, +2)] // HMBL = -2 -> object moves 2 colour clocks right
    public async Task HmblMovesTheBallBySignedAmountAfterHmove(byte hmbl, int expectedDelta)
    {
        var tia = NewTia();
        Write(tia, Ctrlpf, 0x00);
        Write(tia, Hmclr, 0);
        Write(tia, Enabl, EnableD1);
        PositionBall(tia, 60);

        var before = FirstLit(ScanBall(tia));

        // Issue a single HMOVE (with HMBL loaded) during the next HBLANK.
        while (!tia.Blk)
        {
            Tick(tia);
        }

        Write(tia, Hmbl, hmbl);
        Write(tia, Hmove, 0);

        // The HMOVE line itself starts its visible region 8 colour clocks
        // late, so skip it and measure the settled line.
        ScanBall(tia);
        var after = FirstLit(ScanBall(tia));

        await Assert.That(before).IsGreaterThanOrEqualTo(0);
        await Assert.That(after).IsGreaterThanOrEqualTo(0);
        await Assert.That(after - before).IsEqualTo(expectedDelta);
    }

    [Test]
    public async Task BallUsesColupfAndSitsBelowPlayersInNormalPriority()
    {
        var tia = NewTia();
        Write(tia, Colup0, ColorLuma(3, 6));
        Write(tia, Colupf, ColorLuma(4, 4));

        // Ball alone: it takes COLUPF.
        tia.ResolveVideoOutput(player0: false, player1: false, playfield: false, ball: true, pastScreenCentre: false);
        await Assert.That(tia.Col).IsEqualTo((byte)4);
        await Assert.That(tia.Lum).IsEqualTo((byte)4);

        // Ball under a player, normal priority: the player wins.
        tia.ResolveVideoOutput(player0: true, player1: false, playfield: false, ball: true, pastScreenCentre: false);
        await Assert.That(tia.Col).IsEqualTo((byte)3);
        await Assert.That(tia.Lum).IsEqualTo((byte)6);

        // Score mode does not recolour the ball - it stays COLUPF.
        Write(tia, Ctrlpf, CtrlpfScore);
        tia.ResolveVideoOutput(player0: false, player1: false, playfield: false, ball: true, pastScreenCentre: false);
        await Assert.That(tia.Col).IsEqualTo((byte)4);
        await Assert.That(tia.Lum).IsEqualTo((byte)4);
    }

    [Test]
    public async Task CtrlpfPriorityPutsTheBallAbovePlayers()
    {
        var tia = NewTia();
        Write(tia, Colup0, ColorLuma(3, 6));
        Write(tia, Colupf, ColorLuma(4, 4));
        Write(tia, Ctrlpf, CtrlpfPriority);

        tia.ResolveVideoOutput(player0: true, player1: false, playfield: false, ball: true, pastScreenCentre: false);

        // PFP: the ball's COLUPF beats player 0.
        await Assert.That(tia.Col).IsEqualTo((byte)4);
        await Assert.That(tia.Lum).IsEqualTo((byte)4);
    }
}
