using System.Collections.Generic;
using System.Threading.Tasks;
using Aemula.Emulation.Chips.Tia;

namespace Aemula.Tests.Emulation.Chips.Tia;

// Unit-level missile tests. They drive a bare TiaChip: Osc pulses step the
// colour-clock/horizontal machinery, and register writes go straight in via
// a CS1-selected Phi2 edge (independent of Osc timing), exactly as
// TiaCompositingTests does. Each test positions missile 0 with a RESM0
// strobe partway into a line, lets it settle, then scans a whole visible
// region reading PlayerAndMissile0.MissilePixelOn per colour clock.
//
// Positions are asserted differentially (delta between two configs) wherever
// an absolute pixel column would depend on TIA's internal counter phase.
public class TiaMissileTests
{
    private const byte Nusiz0 = 0x04;
    private const byte Resp0 = 0x10;
    private const byte Resm0 = 0x12;
    private const byte Grp0 = 0x1B;
    private const byte Enam0 = 0x1D;
    private const byte Hmm0 = 0x22;
    private const byte Resmp0 = 0x28;
    private const byte Hmove = 0x2A;
    private const byte Hmclr = 0x2B;

    private const byte EnableD1 = 0b10;

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

    // One master OSC pulse == one colour clock.
    private static void Tick(TiaChip tia)
    {
        tia.Osc = false;
        tia.Osc = true;

        // The system renders each colour clock after applying that tick's
        // 6507 bus write; mirror that here so a Write() before this Tick() is
        // visible on the pixel this Tick() produces.
        tia.RenderColorClock();
    }

    // Advance to the first visible (non-blank) colour clock of the next line,
    // passing through the HBLANK in between.
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

    // Scan one whole visible region, returning missile 0's lit flag for each
    // colour clock in it.
    private static bool[] ScanMissile0(TiaChip tia)
    {
        NextLine(tia);

        var lit = new List<bool>();
        while (!tia.Blk)
        {
            lit.Add(tia.PlayerAndMissile0.MissilePixelOn);
            Tick(tia);
        }

        return lit.ToArray();
    }

    private static bool[] ScanPlayer0(TiaChip tia)
    {
        NextLine(tia);

        var lit = new List<bool>();
        while (!tia.Blk)
        {
            lit.Add(tia.PlayerAndMissile0.PixelOn);
            Tick(tia);
        }

        return lit.ToArray();
    }

    // Strobe RESM0 atX colour clocks into a visible line, then let the
    // missile counter self-settle over two more lines so it sits at a stable
    // column near atX on the lines scanned afterwards.
    private static void PositionMissile0(TiaChip tia, int atX)
    {
        NextLine(tia);
        for (var i = 0; i < atX; i++)
        {
            Tick(tia);
        }

        Write(tia, Resm0, 0);

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

    // Number of separate lit runs (missile copies) in a scan.
    private static int RunCount(bool[] scan)
    {
        var runs = 0;
        var prev = false;
        foreach (var b in scan)
        {
            if (b && !prev)
            {
                runs++;
            }

            prev = b;
        }

        return runs;
    }

    // Longest contiguous lit run in a scan.
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
    public async Task EnamGatesTheMissilePixel()
    {
        var tia = NewTia();
        Write(tia, Nusiz0, 0x00);
        Write(tia, Hmclr, 0);
        PositionMissile0(tia, 40);

        Write(tia, Enam0, EnableD1);
        await Assert.That(LitCount(ScanMissile0(tia))).IsGreaterThan(0);

        Write(tia, Enam0, 0);
        await Assert.That(LitCount(ScanMissile0(tia))).IsEqualTo(0);

        // D0 must not enable it - only D1 does.
        Write(tia, Enam0, 0b01);
        await Assert.That(LitCount(ScanMissile0(tia))).IsEqualTo(0);
    }

    [Test]
    [Arguments(0x00, 1)]
    [Arguments(0x10, 2)]
    [Arguments(0x20, 4)]
    [Arguments(0x30, 8)]
    public async Task MissileWidthFollowsNusizD4D5(byte nusiz, int expectedWidth)
    {
        var tia = NewTia();
        Write(tia, Nusiz0, nusiz);
        Write(tia, Hmclr, 0);
        Write(tia, Enam0, EnableD1);
        PositionMissile0(tia, 40);

        var scan = ScanMissile0(tia);

        await Assert.That(LongestRun(scan)).IsEqualTo(expectedWidth);
    }

    [Test]
    public async Task ResmSetsTheMissileColumn()
    {
        var tia = NewTia();
        Write(tia, Nusiz0, 0x00);
        Write(tia, Hmclr, 0);
        Write(tia, Enam0, EnableD1);

        PositionMissile0(tia, 30);
        var near30 = FirstLit(ScanMissile0(tia));

        PositionMissile0(tia, 70);
        var near70 = FirstLit(ScanMissile0(tia));

        await Assert.That(near30).IsGreaterThanOrEqualTo(0);
        await Assert.That(near70).IsGreaterThanOrEqualTo(0);

        // Moving the strobe 40 colour clocks later moves the missile 40
        // colour clocks right.
        await Assert.That(near70 - near30).IsEqualTo(40);
    }

    [Test]
    [Arguments(0x20, -2)] // HMM = +2 -> object moves 2 colour clocks left
    [Arguments(0xE0, +2)] // HMM = -2 -> object moves 2 colour clocks right
    public async Task HmmMovesTheMissileBySignedAmountAfterHmove(byte hmm, int expectedDelta)
    {
        var tia = NewTia();
        Write(tia, Nusiz0, 0x00);
        Write(tia, Hmclr, 0);
        Write(tia, Enam0, EnableD1);
        PositionMissile0(tia, 60);

        var before = FirstLit(ScanMissile0(tia));

        // Issue a single HMOVE (with HMM loaded) during the next HBLANK.
        while (!tia.Blk)
        {
            Tick(tia);
        }

        Write(tia, Hmm0, hmm);
        Write(tia, Hmove, 0);

        // The HMOVE line itself starts its visible region 8 colour clocks
        // late, so skip it and measure the settled line, whose origin is
        // back to normal.
        ScanMissile0(tia);
        var after = FirstLit(ScanMissile0(tia));

        await Assert.That(before).IsGreaterThanOrEqualTo(0);
        await Assert.That(after).IsGreaterThanOrEqualTo(0);
        await Assert.That(after - before).IsEqualTo(expectedDelta);
    }

    [Test]
    [Arguments(0x00, 1)] // one copy
    [Arguments(0x01, 2)] // two copies, close
    [Arguments(0x03, 3)] // three copies, close
    public async Task MissileCopiesTrackNusizD0D2(byte nusiz, int expectedCopies)
    {
        var tia = NewTia();
        Write(tia, Nusiz0, nusiz);
        Write(tia, Hmclr, 0);
        Write(tia, Enam0, EnableD1);
        PositionMissile0(tia, 20);

        await Assert.That(RunCount(ScanMissile0(tia))).IsEqualTo(expectedCopies);
    }

    [Test]
    public async Task ResmpSuppressesTheMissileAndRecentresItOnUnlock()
    {
        var tia = NewTia();
        Write(tia, Nusiz0, 0x00);
        Write(tia, Hmclr, 0);
        Write(tia, Enam0, EnableD1);

        // Park player 0 somewhere on the line and give it a solid graphic so
        // ScanPlayer0 reports its span.
        Write(tia, Grp0, 0xFF);
        NextLine(tia);
        for (var i = 0; i < 50; i++)
        {
            Tick(tia);
        }

        Write(tia, Resp0, 0);
        NextLine(tia);
        NextLine(tia);

        // Missile visible before the lock.
        await Assert.That(LitCount(ScanMissile0(tia))).IsGreaterThan(0);

        // Locked: the missile pixel is forced off.
        Write(tia, Resmp0, EnableD1);
        await Assert.That(LitCount(ScanMissile0(tia))).IsEqualTo(0);
        await Assert.That(LitCount(ScanMissile0(tia))).IsEqualTo(0);

        // Unlocked: it comes back, aligned to the player. The counter was
        // held equal to the player's while locked, so on release the missile
        // lands on the player copy start (this model's approximation of
        // "centred on the player" - see PlayerAndMissile.UpdateMissileDiv4).
        Write(tia, Resmp0, 0);
        var missile = ScanMissile0(tia);
        var player = ScanPlayer0(tia);

        await Assert.That(LitCount(missile)).IsGreaterThan(0);

        var missileStart = FirstLit(missile);
        var playerStart = FirstLit(player);
        await Assert.That(playerStart).IsGreaterThanOrEqualTo(0);
        await Assert.That(missileStart - playerStart).IsEqualTo(0);
    }
}
