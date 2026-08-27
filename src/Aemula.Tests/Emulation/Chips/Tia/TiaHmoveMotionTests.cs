using System.Collections.Generic;
using System.Threading.Tasks;
using Aemula.Emulation.Chips.Tia;

namespace Aemula.Tests.Emulation.Chips.Tia;

// The one HMOVE property the other Tia test files don't pin down exactly:
// how far each of the 16 HMxx codes actually moves an object. Same harness
// shape as TiaMissileTests / TiaNusizScalingTests - Osc pulses step the
// colour-clock machinery, register writes go in via a CS1-selected Phi2
// edge.
//
// HMxx is a signed 4-bit value in the register's top nibble: $0 = no
// motion, $1..$7 move the object 1..7 colour clocks left, $8..$F move it
// 8..1 clocks right. "Left" means the object's first pixel lands at a lower
// column, so the measured (first-pixel column) delta is the negation of that
// signed amount: $1 -> -1, $F -> +1, $8 -> +8.
//
// Method: position the object with RESx partway into a line, settle it over
// two lines, record its first-pixel column. Then strobe HMxx + HMOVE just
// after a line starts (inside HBLANK), skip the HMOVE line itself (its
// visible region starts 8 colour clocks late - the comb, covered by
// TiaHmoveCombTests), and measure the object's first-pixel column on the
// next, settled line. The delta between the two columns is the motion.
//
// Players get all 16 codes; missile 0 and the ball get a representative
// spread (zero, small-left, max-left, max-right, small-right).
public class TiaHmoveMotionTests
{
    private const byte Nusiz0 = 0x04;
    private const byte Ctrlpf = 0x0A;
    private const byte Resp0 = 0x10;
    private const byte Resm0 = 0x12;
    private const byte Resbl = 0x14;
    private const byte Grp0 = 0x1B;
    private const byte Enam0 = 0x1D;
    private const byte Enabl = 0x1F;
    private const byte Hmp0 = 0x20;
    private const byte Hmm0 = 0x22;
    private const byte Hmbl = 0x24;
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
    }

    // Advance to the first visible (non-blank) colour clock of the next line.
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

    private static bool[] Scan(TiaChip tia, System.Func<TiaChip, bool> pixelOn)
    {
        NextLine(tia);

        var lit = new List<bool>();
        while (!tia.Blk)
        {
            lit.Add(pixelOn(tia));
            Tick(tia);
        }

        return lit.ToArray();
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

    // Strobe a reset atX colour clocks into a visible line, then let the
    // object's counter self-settle over two lines so it sits at a stable
    // column on the lines scanned afterwards.
    private static void PositionObject(TiaChip tia, byte resetAddress, int atX)
    {
        NextLine(tia);
        for (var i = 0; i < atX; i++)
        {
            Tick(tia);
        }

        Write(tia, resetAddress, 0);

        NextLine(tia);
        NextLine(tia);
    }

    // Baseline first-pixel column, then first-pixel column on the settled
    // line after HMxx + HMOVE, returned as (before, after).
    private static (int Before, int After) MeasureShift(
        TiaChip tia, byte hmAddress, int hmNibble, System.Func<TiaChip, bool> pixelOn)
    {
        var before = FirstLit(Scan(tia, pixelOn));

        // Now sitting on the first HBLANK colour clock after that scan.
        Write(tia, hmAddress, (byte)(hmNibble << 4));
        Write(tia, Hmove, 0);

        Scan(tia, pixelOn); // discard the combed HMOVE line
        var after = FirstLit(Scan(tia, pixelOn));

        return (before, after);
    }

    [Test]
    [Arguments(0x0, 0)]
    [Arguments(0x1, -1)]
    [Arguments(0x2, -2)]
    [Arguments(0x3, -3)]
    [Arguments(0x4, -4)]
    [Arguments(0x5, -5)]
    [Arguments(0x6, -6)]
    [Arguments(0x7, -7)]
    [Arguments(0x8, +8)]
    [Arguments(0x9, +7)]
    [Arguments(0xA, +6)]
    [Arguments(0xB, +5)]
    [Arguments(0xC, +4)]
    [Arguments(0xD, +3)]
    [Arguments(0xE, +2)]
    [Arguments(0xF, +1)]
    public async Task Hmp0ShiftsPlayer0BySignedNibble(int hmNibble, int expectedShift)
    {
        var tia = NewTia();
        Write(tia, Nusiz0, 0x00);
        Write(tia, Grp0, 0x80); // single bit -> single one-pixel run
        Write(tia, Hmclr, 0);
        PositionObject(tia, Resp0, 50);

        var (before, after) = MeasureShift(tia, Hmp0, hmNibble, t => t.PlayerAndMissile0.PixelOn);

        await Assert.That(before).IsGreaterThanOrEqualTo(0);
        await Assert.That(after).IsGreaterThanOrEqualTo(0);
        await Assert.That(after - before).IsEqualTo(expectedShift);
    }

    [Test]
    [Arguments(0x0, 0)]
    [Arguments(0x1, -1)]
    [Arguments(0x3, -3)]
    [Arguments(0x7, -7)]
    [Arguments(0x8, +8)]
    [Arguments(0xC, +4)]
    [Arguments(0xF, +1)]
    public async Task Hmm0ShiftsMissile0BySignedNibble(int hmNibble, int expectedShift)
    {
        var tia = NewTia();
        Write(tia, Nusiz0, 0x00);
        Write(tia, Enam0, EnableD1);
        Write(tia, Hmclr, 0);
        PositionObject(tia, Resm0, 50);

        var (before, after) = MeasureShift(tia, Hmm0, hmNibble, t => t.PlayerAndMissile0.MissilePixelOn);

        await Assert.That(before).IsGreaterThanOrEqualTo(0);
        await Assert.That(after).IsGreaterThanOrEqualTo(0);
        await Assert.That(after - before).IsEqualTo(expectedShift);
    }

    [Test]
    [Arguments(0x0, 0)]
    [Arguments(0x1, -1)]
    [Arguments(0x3, -3)]
    [Arguments(0x7, -7)]
    [Arguments(0x8, +8)]
    [Arguments(0xC, +4)]
    [Arguments(0xF, +1)]
    public async Task HmblShiftsBallBySignedNibble(int hmNibble, int expectedShift)
    {
        var tia = NewTia();
        Write(tia, Ctrlpf, 0x00);
        Write(tia, Enabl, EnableD1);
        Write(tia, Hmclr, 0);
        PositionObject(tia, Resbl, 50);

        var (before, after) = MeasureShift(tia, Hmbl, hmNibble, t => t.Ball.PixelOn);

        await Assert.That(before).IsGreaterThanOrEqualTo(0);
        await Assert.That(after).IsGreaterThanOrEqualTo(0);
        await Assert.That(after - before).IsEqualTo(expectedShift);
    }
}
