using System.Collections.Generic;
using System.Threading.Tasks;
using Aemula.Emulation.Chips.Tia;

namespace Aemula.Tests.Emulation.Chips.Tia;

// Unit-level vertical-delay tests, same bare-TiaChip harness as
// TiaMissileTests / TiaBallTests: Osc pulses step the colour-clock machinery,
// register writes go in via a CS1-selected Phi2 edge.
//
// The hardware model under test: each player has two graphics latches ("new"
// written by its own GRPx strobe, "old" a deferred copy), and the ball has a
// matching enable pair. VDELPx / VDELBL (D0) is a display-time mux - the
// drawing path reads "old" while it is set, "new" while it is clear. The "old"
// latch is clocked by the *other* object's GRP strobe:
//   - GRP0 write  -> P0.new = data,  P1.old = P1.new
//   - GRP1 write  -> P1.new = data,  P0.old = P0.new,  Ball.old = Ball.new
// ENABL writes Ball.new; it never clocks Ball.old.
public class TiaVerticalDelayTests
{
    private const byte Nusiz0 = 0x04;
    private const byte Nusiz1 = 0x05;
    private const byte Ctrlpf = 0x0A;
    private const byte Resp0 = 0x10;
    private const byte Resp1 = 0x11;
    private const byte Resbl = 0x14;
    private const byte Grp0 = 0x1B;
    private const byte Grp1 = 0x1C;
    private const byte Enabl = 0x1F;
    private const byte Vdelp0 = 0x25;
    private const byte Vdelp1 = 0x26;
    private const byte Vdelbl = 0x27;
    private const byte Hmclr = 0x2B;

    private const byte EnableD1 = 0b10;
    private const byte VdelD0 = 0b01;

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

    private static int LitCount(IEnumerable<bool> scan)
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

    private static bool[] ScanPlayer1(TiaChip tia)
    {
        NextLine(tia);

        var lit = new List<bool>();
        while (!tia.Blk)
        {
            lit.Add(tia.PlayerAndMissile1.PixelOn);
            Tick(tia);
        }

        return lit.ToArray();
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

    // Strobe RESPx atX colour clocks into a visible line, then let the counter
    // self-settle over two more lines so the copy sits at a stable column.
    private static void PositionPlayer(TiaChip tia, byte resAddress, int atX)
    {
        NextLine(tia);
        for (var i = 0; i < atX; i++)
        {
            Tick(tia);
        }

        Write(tia, resAddress, 0);

        NextLine(tia);
        NextLine(tia);
    }

    [Test]
    public async Task Vdelp0Clear_Grp0WriteShowsImmediately()
    {
        var tia = NewTia();
        Write(tia, Nusiz0, 0x00);
        Write(tia, Hmclr, 0);
        Write(tia, Vdelp0, 0x00);
        PositionPlayer(tia, Resp0, 40);

        // A solid graphic written with VDELP0 clear is on screen the same line.
        Write(tia, Grp0, 0xFF);
        await Assert.That(LitCount(ScanPlayer0(tia))).IsEqualTo(8);

        // Clearing it is equally immediate.
        Write(tia, Grp0, 0x00);
        await Assert.That(LitCount(ScanPlayer0(tia))).IsEqualTo(0);
    }

    [Test]
    public async Task Vdelp0Set_DisplayedGraphicChangesOnlyAfterGrp1Write()
    {
        var tia = NewTia();
        Write(tia, Nusiz0, 0x00);
        Write(tia, Hmclr, 0);
        Write(tia, Vdelp0, VdelD0);
        PositionPlayer(tia, Resp0, 40);

        // GRP0 lands in "new"; the drawing path is reading "old" (still 0).
        Write(tia, Grp0, 0xFF);
        await Assert.That(LitCount(ScanPlayer0(tia))).IsEqualTo(0);

        // A GRP1 write copies P0's new -> old: now the graphic appears.
        Write(tia, Grp1, 0x12);
        await Assert.That(LitCount(ScanPlayer0(tia))).IsEqualTo(8);

        // Same lag on the way down: GRP0 clears "new" but "old" still shows.
        Write(tia, Grp0, 0x00);
        await Assert.That(LitCount(ScanPlayer0(tia))).IsEqualTo(8);

        Write(tia, Grp1, 0x00);
        await Assert.That(LitCount(ScanPlayer0(tia))).IsEqualTo(0);
    }

    [Test]
    public async Task Vdelp1Set_DisplayedGraphicChangesOnlyAfterGrp0Write()
    {
        var tia = NewTia();
        Write(tia, Nusiz1, 0x00);
        Write(tia, Hmclr, 0);
        Write(tia, Vdelp1, VdelD0);
        PositionPlayer(tia, Resp1, 40);

        // Symmetric to player 0: GRP1 fills P1's "new", GRP0 clocks its "old".
        Write(tia, Grp1, 0xFF);
        await Assert.That(LitCount(ScanPlayer1(tia))).IsEqualTo(0);

        Write(tia, Grp0, 0x34);
        await Assert.That(LitCount(ScanPlayer1(tia))).IsEqualTo(8);
    }

    [Test]
    public async Task VdelblSet_BallEnableIsHeldUntilGrp1Write()
    {
        var tia = NewTia();
        Write(tia, Ctrlpf, 0x00);
        Write(tia, Hmclr, 0);
        Write(tia, Vdelbl, VdelD0);
        PositionPlayer(tia, Resbl, 40);

        // ENABL sets the ball's "new" enable; "old" is what VDELBL displays.
        Write(tia, Enabl, EnableD1);
        await Assert.That(LitCount(ScanBall(tia))).IsEqualTo(0);

        // GRP0 does NOT clock the ball's "old" latch - still off.
        Write(tia, Grp0, 0x00);
        await Assert.That(LitCount(ScanBall(tia))).IsEqualTo(0);

        // GRP1 does: new -> old, and the ball lights.
        Write(tia, Grp1, 0x00);
        await Assert.That(LitCount(ScanBall(tia))).IsGreaterThan(0);
    }

    [Test]
    public async Task EnablingVdelp0AfterAGrp0WriteRevealsTheStaleOldLatch()
    {
        var tia = NewTia();
        Write(tia, Nusiz0, 0x00);
        Write(tia, Hmclr, 0);
        Write(tia, Vdelp0, 0x00);
        PositionPlayer(tia, Resp0, 40);

        // Written and displayed with the delay off.
        Write(tia, Grp0, 0xFF);
        await Assert.That(LitCount(ScanPlayer0(tia))).IsEqualTo(8);

        // Turning the delay on now switches the mux to "old", which was never
        // latched - so the player vanishes until a GRP1 strobe copies new->old.
        Write(tia, Vdelp0, VdelD0);
        await Assert.That(LitCount(ScanPlayer0(tia))).IsEqualTo(0);

        Write(tia, Grp1, 0x00);
        await Assert.That(LitCount(ScanPlayer0(tia))).IsEqualTo(8);
    }

    [Test]
    public async Task VerticalDelayFlagsAreIndependentAndTrackOnlyD0()
    {
        var tia = NewTia();

        Write(tia, Vdelp0, VdelD0);
        Write(tia, Vdelp1, 0x00);
        Write(tia, Vdelbl, VdelD0);

        await Assert.That(tia.PlayerAndMissile0.VerticalDelay).IsTrue();
        await Assert.That(tia.PlayerAndMissile1.VerticalDelay).IsFalse();
        await Assert.That(tia.Ball.VerticalDelay).IsTrue();

        // Flipping one leaves the others alone.
        Write(tia, Vdelp1, VdelD0);
        Write(tia, Vdelp0, 0x00);

        await Assert.That(tia.PlayerAndMissile0.VerticalDelay).IsFalse();
        await Assert.That(tia.PlayerAndMissile1.VerticalDelay).IsTrue();
        await Assert.That(tia.Ball.VerticalDelay).IsTrue();

        // Only D0 counts - D1 set with D0 clear is still "no delay".
        Write(tia, Vdelbl, 0b10);
        await Assert.That(tia.Ball.VerticalDelay).IsFalse();
    }
}
