using System.Collections.Generic;
using System.Threading.Tasks;
using Aemula.Emulation.Chips.Tia;

namespace Aemula.Tests.Emulation.Chips.Tia;

// TIA collision-latch tests. Two layers:
//
//  * CollisionLatches on its own, driven with hand-built ObjectPixels - the
//    15-pair accumulate map, stickiness, and Clear.
//  * A bare TiaChip (CS1-selected, Osc-stepped, same harness shape as
//    TiaBallTests / TiaMissileTests) for the CX register read decode, CXCLR,
//    and the active-display gating on Accumulate.
//
// The read decode is checked by seeding TiaChip.Collisions directly and
// reading it back through a Phi2 read edge; the pipeline test additionally
// overlaps two real players end to end.
public class TiaCollisionTests
{
    private const byte Vblank = 0x01;
    private const byte Nusiz0 = 0x04;
    private const byte Nusiz1 = 0x05;
    private const byte Resp0 = 0x10;
    private const byte Resp1 = 0x11;
    private const byte Grp0 = 0x1B;
    private const byte Grp1 = 0x1C;
    private const byte Cxclr = 0x2C;

    private const byte Cxm0p = 0x30;
    private const byte Cxm1p = 0x31;
    private const byte Cxp0fb = 0x32;
    private const byte Cxp1fb = 0x33;
    private const byte Cxm0fb = 0x34;
    private const byte Cxm1fb = 0x35;
    private const byte Cxblpf = 0x36;
    private const byte Cxppmm = 0x37;

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

    // Perform a register read edge and return TIA's Data67 (the value the
    // system ORs onto D6/D7 of the CPU bus).
    private static byte ReadData67(TiaChip tia, byte address)
    {
        tia.RW = true;
        tia.Address = address;
        tia.Phi2 = false;
        tia.Phi2 = true;
        return tia.Data67;
    }

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

    // Tick through one whole visible region so DoVideo accumulates collisions
    // for it.
    private static void ScanVisibleLine(TiaChip tia)
    {
        NextLine(tia);
        while (!tia.Blk)
        {
            Tick(tia);
        }
    }

    // Strobe RESP0 and RESP1 at the same beam position so the two players
    // draw on top of each other, then let the counters settle.
    private static void OverlapPlayers(TiaChip tia, int atX)
    {
        NextLine(tia);
        for (var i = 0; i < atX; i++)
        {
            Tick(tia);
        }

        Write(tia, Resp0, 0);
        Write(tia, Resp1, 0);

        NextLine(tia);
        NextLine(tia);
    }

    private static ObjectPixels AllObjects() => new()
    {
        Player0 = true,
        Missile0 = true,
        Player1 = true,
        Missile1 = true,
        Playfield = true,
        Ball = true,
    };

    private static bool Bit(byte value, int index) => (value & (1 << index)) != 0;

    [Test]
    public async Task AccumulateSetsExactlyTheOverlappingPairBit()
    {
        var cases = new List<(ObjectPixels pixels, ushort expected)>
        {
            (new ObjectPixels { Missile0 = true, Player0 = true }, CollisionLatches.M0P0),
            (new ObjectPixels { Missile0 = true, Player1 = true }, CollisionLatches.M0P1),
            (new ObjectPixels { Missile1 = true, Player0 = true }, CollisionLatches.M1P0),
            (new ObjectPixels { Missile1 = true, Player1 = true }, CollisionLatches.M1P1),
            (new ObjectPixels { Player0 = true, Playfield = true }, CollisionLatches.P0PF),
            (new ObjectPixels { Player0 = true, Ball = true }, CollisionLatches.P0BL),
            (new ObjectPixels { Player1 = true, Playfield = true }, CollisionLatches.P1PF),
            (new ObjectPixels { Player1 = true, Ball = true }, CollisionLatches.P1BL),
            (new ObjectPixels { Missile0 = true, Playfield = true }, CollisionLatches.M0PF),
            (new ObjectPixels { Missile0 = true, Ball = true }, CollisionLatches.M0BL),
            (new ObjectPixels { Missile1 = true, Playfield = true }, CollisionLatches.M1PF),
            (new ObjectPixels { Missile1 = true, Ball = true }, CollisionLatches.M1BL),
            (new ObjectPixels { Ball = true, Playfield = true }, CollisionLatches.BLPF),
            (new ObjectPixels { Player0 = true, Player1 = true }, CollisionLatches.P0P1),
            (new ObjectPixels { Missile0 = true, Missile1 = true }, CollisionLatches.M0M1),
        };

        // Every one of the 15 named pairs is covered above.
        await Assert.That(cases.Count).IsEqualTo(15);

        foreach (var (pixels, expected) in cases)
        {
            var latches = new CollisionLatches();
            latches.Accumulate(pixels);

            for (var bit = 0; bit < 15; bit++)
            {
                var mask = (ushort)(1 << bit);
                await Assert.That(latches.IsSet(mask)).IsEqualTo(mask == expected);
            }
        }
    }

    [Test]
    public async Task AccumulateSetsEveryPairPresentInOneClock()
    {
        var latches = new CollisionLatches();

        // P0, M0 and the playfield all coincide here.
        latches.Accumulate(new ObjectPixels { Player0 = true, Missile0 = true, Playfield = true });

        await Assert.That(latches.IsSet(CollisionLatches.P0PF)).IsTrue();
        await Assert.That(latches.IsSet(CollisionLatches.M0PF)).IsTrue();
        await Assert.That(latches.IsSet(CollisionLatches.M0P0)).IsTrue();

        // Pairs that need the ball / player 1 must stay clear.
        await Assert.That(latches.IsSet(CollisionLatches.P0BL)).IsFalse();
        await Assert.That(latches.IsSet(CollisionLatches.P0P1)).IsFalse();
    }

    [Test]
    public async Task LatchesAreStickyUntilCleared()
    {
        var latches = new CollisionLatches();
        latches.Accumulate(new ObjectPixels { Player0 = true, Ball = true });
        await Assert.That(latches.IsSet(CollisionLatches.P0BL)).IsTrue();

        // Later clocks with the overlap gone must not drop the latch.
        latches.Accumulate(new ObjectPixels { Player0 = true });
        latches.Accumulate(default);
        await Assert.That(latches.IsSet(CollisionLatches.P0BL)).IsTrue();

        latches.Clear();
        await Assert.That(latches.IsSet(CollisionLatches.P0BL)).IsFalse();
    }

    [Test]
    [Arguments(Cxm0p, /* d7 */ CollisionLatches.M0P1, /* d6 */ CollisionLatches.M0P0)]
    [Arguments(Cxm1p, CollisionLatches.M1P0, CollisionLatches.M1P1)]
    [Arguments(Cxp0fb, CollisionLatches.P0PF, CollisionLatches.P0BL)]
    [Arguments(Cxp1fb, CollisionLatches.P1PF, CollisionLatches.P1BL)]
    [Arguments(Cxm0fb, CollisionLatches.M0PF, CollisionLatches.M0BL)]
    [Arguments(Cxm1fb, CollisionLatches.M1PF, CollisionLatches.M1BL)]
    [Arguments(Cxppmm, CollisionLatches.P0P1, CollisionLatches.M0M1)]
    public async Task RegisterReadMapsD7AndD6ToTheRightLatches(byte address, ushort d7Pair, ushort d6Pair)
    {
        // D7 latch alone -> Data67 bit 1.
        var tia = NewTia();
        tia.Collisions.Accumulate(PairPixels(d7Pair));
        await Assert.That(ReadData67(tia, address)).IsEqualTo((byte)0b10);

        // D6 latch alone -> Data67 bit 0.
        tia = NewTia();
        tia.Collisions.Accumulate(PairPixels(d6Pair));
        await Assert.That(ReadData67(tia, address)).IsEqualTo((byte)0b01);

        // Both -> both bits.
        tia = NewTia();
        tia.Collisions.Accumulate(PairPixels(d7Pair));
        tia.Collisions.Accumulate(PairPixels(d6Pair));
        await Assert.That(ReadData67(tia, address)).IsEqualTo((byte)0b11);
    }

    [Test]
    public async Task CxblpfReportsOnlyD7AndNeverD6()
    {
        var tia = NewTia();

        // Force every latch on.
        tia.Collisions.Accumulate(AllObjects());

        // BL/PF on D7, D6 unused -> 0b10 even with everything set.
        await Assert.That(ReadData67(tia, Cxblpf)).IsEqualTo((byte)0b10);
    }

    [Test]
    public async Task CxclrWriteClearsEveryLatch()
    {
        var tia = NewTia();
        tia.Collisions.Accumulate(AllObjects());

        // Sanity: something is latched.
        await Assert.That(ReadData67(tia, Cxppmm)).IsEqualTo((byte)0b11);

        Write(tia, Cxclr, 0);

        for (byte address = Cxm0p; address <= Cxppmm; address++)
        {
            await Assert.That(ReadData67(tia, address)).IsEqualTo((byte)0);
        }
    }

    [Test]
    public async Task ReadOfANonCollisionAddressLeavesData67Untouched()
    {
        var tia = NewTia();
        tia.Collisions.Accumulate(AllObjects());

        tia.Data67 = 0b11;
        tia.RW = true;
        tia.Address = 0x3F; // not a CX register
        tia.Phi2 = false;
        tia.Phi2 = true;

        await Assert.That(tia.Data67).IsEqualTo((byte)0b11);
    }

    [Test]
    public async Task OverlappingPlayersLatchOnlyCxppmmD7AndStaySet()
    {
        var tia = NewTia();
        Write(tia, Nusiz0, 0x00);
        Write(tia, Nusiz1, 0x00);
        Write(tia, Grp0, 0xFF);
        Write(tia, Grp1, 0xFF);

        OverlapPlayers(tia, 40);
        ScanVisibleLine(tia);

        // P0 x P1 -> CXPPMM D7, and nothing else.
        await Assert.That(Bit(ReadData67(tia, Cxppmm), 1)).IsTrue();
        await Assert.That(Bit(ReadData67(tia, Cxppmm), 0)).IsFalse(); // M0 x M1
        foreach (var address in new byte[] { Cxm0p, Cxm1p, Cxp0fb, Cxp1fb, Cxm0fb, Cxm1fb, Cxblpf })
        {
            await Assert.That(ReadData67(tia, address)).IsEqualTo((byte)0);
        }

        // Sticky: pull both graphics low, scan more lines, the latch holds.
        Write(tia, Grp0, 0x00);
        Write(tia, Grp1, 0x00);
        ScanVisibleLine(tia);
        ScanVisibleLine(tia);
        await Assert.That(Bit(ReadData67(tia, Cxppmm), 1)).IsTrue();
    }

    [Test]
    public async Task NoCollisionIsRegisteredWhileBlanked()
    {
        var tia = NewTia();
        Write(tia, Nusiz0, 0x00);
        Write(tia, Nusiz1, 0x00);
        Write(tia, Grp0, 0xFF);
        Write(tia, Grp1, 0xFF);
        OverlapPlayers(tia, 40);

        // Baseline: in active display the overlap latches.
        ScanVisibleLine(tia);
        await Assert.That(Bit(ReadData67(tia, Cxppmm), 1)).IsTrue();
        Write(tia, Cxclr, 0);

        // Force vertical blank and run several lines' worth of colour clocks.
        // The players still overlap every line, but Accumulate is gated off.
        Write(tia, Vblank, 0b10);
        for (var i = 0; i < 5 * 228; i++)
        {
            Tick(tia);
        }

        await Assert.That(ReadData67(tia, Cxppmm)).IsEqualTo((byte)0);

        // Clearing blank lets it register again - the gate, not a breakage.
        Write(tia, Vblank, 0x00);
        ScanVisibleLine(tia);
        await Assert.That(Bit(ReadData67(tia, Cxppmm), 1)).IsTrue();
    }

    // Build an ObjectPixels that lights exactly the two objects of a named
    // pair, so Accumulate sets that one latch.
    private static ObjectPixels PairPixels(ushort pair) => pair switch
    {
        CollisionLatches.M0P0 => new ObjectPixels { Missile0 = true, Player0 = true },
        CollisionLatches.M0P1 => new ObjectPixels { Missile0 = true, Player1 = true },
        CollisionLatches.M1P0 => new ObjectPixels { Missile1 = true, Player0 = true },
        CollisionLatches.M1P1 => new ObjectPixels { Missile1 = true, Player1 = true },
        CollisionLatches.P0PF => new ObjectPixels { Player0 = true, Playfield = true },
        CollisionLatches.P0BL => new ObjectPixels { Player0 = true, Ball = true },
        CollisionLatches.P1PF => new ObjectPixels { Player1 = true, Playfield = true },
        CollisionLatches.P1BL => new ObjectPixels { Player1 = true, Ball = true },
        CollisionLatches.M0PF => new ObjectPixels { Missile0 = true, Playfield = true },
        CollisionLatches.M0BL => new ObjectPixels { Missile0 = true, Ball = true },
        CollisionLatches.M1PF => new ObjectPixels { Missile1 = true, Playfield = true },
        CollisionLatches.M1BL => new ObjectPixels { Missile1 = true, Ball = true },
        CollisionLatches.BLPF => new ObjectPixels { Ball = true, Playfield = true },
        CollisionLatches.P0P1 => new ObjectPixels { Player0 = true, Player1 = true },
        CollisionLatches.M0M1 => new ObjectPixels { Missile0 = true, Missile1 = true },
        _ => default,
    };
}
