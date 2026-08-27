using System.Collections.Generic;
using System.Threading.Tasks;
using Aemula.Emulation.Chips.Tia;

namespace Aemula.Tests.Emulation.Chips.Tia;

// Priority / score-mode coverage for the single resolver stage
// (TiaChip.ResolveVideoOutput) that replaced the old per-object
// last-writer-wins compositing. Like TiaCompositingTests, most cases drive
// the resolver directly with hand-picked presence bits after setting the
// colour registers through real Phi2 writes; the split-point case drives the
// full colour-clock machinery through Osc because it is about horizontal
// position, not colour.
//
// Reference for the ordering asserted here: the Stella Programmer's Guide
// priority table and Stella's own src/emucore/tia/TIA.cxx renderPixel, which
// give, highest to lowest:
//   normal            P0/M0 -> P1/M1 -> PF -> BL -> BK
//   CTRLPF D2 (PFP)   PF -> BL -> P0/M0 -> P1/M1 -> BK
//   score (CTRLPF D1) P0/M0 -> PF -> P1/M1 -> BL -> BK   (PF takes a player colour)
public class TiaPriorityTests
{
    private const byte Colup0 = 0x06;
    private const byte Colup1 = 0x07;
    private const byte Colupf = 0x08;
    private const byte Colubk = 0x09;
    private const byte Ctrlpf = 0x0A;
    private const byte Pf1 = 0x0E;
    private const byte Pf2 = 0x0F;
    private const byte Resp0 = 0x10;
    private const byte Resbl = 0x14;
    private const byte Grp0 = 0x1B;
    private const byte Enabl = 0x1F;

    private const byte CtrlpfReflect = 0b0000_0001;  // D0
    private const byte CtrlpfScore = 0b0000_0010;    // D1
    private const byte CtrlpfPriority = 0b0000_0100; // D2

    // Distinct hues so every layer is individually identifiable in Col.
    private const int HueP0 = 0x3;
    private const int HueP1 = 0x9;
    private const int HuePf = 0x4;
    private const int HueBk = 0x7;

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

    private static void SetColours(TiaChip tia)
    {
        Write(tia, Colup0, ColorLuma(HueP0, 6));
        Write(tia, Colup1, ColorLuma(HueP1, 2));
        Write(tia, Colupf, ColorLuma(HuePf, 4));
        Write(tia, Colubk, ColorLuma(HueBk, 0));
    }

    [Test]
    public async Task NormalPriorityStacksPlayersOverPlayfieldOverBallOverBackground()
    {
        var tia = NewTia();
        SetColours(tia);
        Write(tia, Ctrlpf, 0);

        // All four layers present -> player 0.
        tia.ResolveVideoOutput(player0: true, player1: true, playfield: true, ball: true, pastScreenCentre: false);
        await Assert.That(tia.Col).IsEqualTo((byte)HueP0);

        // Drop player 0 -> player 1.
        tia.ResolveVideoOutput(player0: false, player1: true, playfield: true, ball: true, pastScreenCentre: false);
        await Assert.That(tia.Col).IsEqualTo((byte)HueP1);

        // Drop both players -> playfield (its own COLUPF).
        tia.ResolveVideoOutput(player0: false, player1: false, playfield: true, ball: true, pastScreenCentre: false);
        await Assert.That(tia.Col).IsEqualTo((byte)HuePf);

        // Playfield off, ball still on -> ball, also COLUPF.
        tia.ResolveVideoOutput(player0: false, player1: false, playfield: false, ball: true, pastScreenCentre: false);
        await Assert.That(tia.Col).IsEqualTo((byte)HuePf);

        // Nothing lit -> background.
        tia.ResolveVideoOutput(player0: false, player1: false, playfield: false, ball: false, pastScreenCentre: false);
        await Assert.That(tia.Col).IsEqualTo((byte)HueBk);
    }

    [Test]
    public async Task PlayfieldPriorityModeStacksPlayfieldGroupOverPlayersOverBackground()
    {
        var tia = NewTia();
        SetColours(tia);
        Write(tia, Ctrlpf, CtrlpfPriority);

        // Everything present -> playfield/ball group wins (COLUPF).
        tia.ResolveVideoOutput(player0: true, player1: true, playfield: true, ball: true, pastScreenCentre: false);
        await Assert.That(tia.Col).IsEqualTo((byte)HuePf);

        // Playfield off but ball on -> still the PF/BL group, still COLUPF.
        tia.ResolveVideoOutput(player0: true, player1: true, playfield: false, ball: true, pastScreenCentre: false);
        await Assert.That(tia.Col).IsEqualTo((byte)HuePf);

        // PF/BL group gone -> player 0.
        tia.ResolveVideoOutput(player0: true, player1: true, playfield: false, ball: false, pastScreenCentre: false);
        await Assert.That(tia.Col).IsEqualTo((byte)HueP0);

        // Only player 1 -> player 1.
        tia.ResolveVideoOutput(player0: false, player1: true, playfield: false, ball: false, pastScreenCentre: false);
        await Assert.That(tia.Col).IsEqualTo((byte)HueP1);

        // Nothing -> background.
        tia.ResolveVideoOutput(player0: false, player1: false, playfield: false, ball: false, pastScreenCentre: false);
        await Assert.That(tia.Col).IsEqualTo((byte)HueBk);
    }

    [Test]
    public async Task ScoreModeTintsPlayfieldPerHalfAndLeavesTheBallOnColupf()
    {
        var tia = NewTia();
        SetColours(tia);
        Write(tia, Ctrlpf, CtrlpfScore);

        // Left half: playfield bit takes COLUP0.
        tia.ResolveVideoOutput(player0: false, player1: false, playfield: true, ball: false, pastScreenCentre: false);
        await Assert.That(tia.Col).IsEqualTo((byte)HueP0);
        await Assert.That(tia.Lum).IsEqualTo((byte)6);

        // Right half: playfield bit takes COLUP1.
        tia.ResolveVideoOutput(player0: false, player1: false, playfield: true, ball: false, pastScreenCentre: true);
        await Assert.That(tia.Col).IsEqualTo((byte)HueP1);
        await Assert.That(tia.Lum).IsEqualTo((byte)2);

        // The ball is never recoloured by score mode - COLUPF in both halves.
        tia.ResolveVideoOutput(player0: false, player1: false, playfield: false, ball: true, pastScreenCentre: false);
        await Assert.That(tia.Col).IsEqualTo((byte)HuePf);
        tia.ResolveVideoOutput(player0: false, player1: false, playfield: false, ball: true, pastScreenCentre: true);
        await Assert.That(tia.Col).IsEqualTo((byte)HuePf);
    }

    [Test]
    public async Task ScoreModePlayfieldOutranksPlayer1ButNotPlayer0()
    {
        var tia = NewTia();
        SetColours(tia);
        Write(tia, Ctrlpf, CtrlpfScore);

        // Left half, playfield over player 1, no player 0: the score-mode
        // playfield borrows COLUP0's slot and wins over player 1.
        tia.ResolveVideoOutput(player0: false, player1: true, playfield: true, ball: false, pastScreenCentre: false);
        await Assert.That(tia.Col).IsEqualTo((byte)HueP0);

        // Player 0 still covers the score-mode playfield.
        tia.ResolveVideoOutput(player0: true, player1: false, playfield: true, ball: false, pastScreenCentre: false);
        await Assert.That(tia.Col).IsEqualTo((byte)HueP0);

        // Contrast: without score mode the same bits give player 1 (PF is the
        // low group again).
        Write(tia, Ctrlpf, 0);
        tia.ResolveVideoOutput(player0: false, player1: true, playfield: true, ball: false, pastScreenCentre: false);
        await Assert.That(tia.Col).IsEqualTo((byte)HueP1);
    }

    [Test]
    public async Task PlayfieldPriorityBitOverridesScoreMode()
    {
        var tia = NewTia();
        SetColours(tia);
        Write(tia, Ctrlpf, (byte)(CtrlpfScore | CtrlpfPriority));

        // D2 set: score is ignored, the playfield keeps COLUPF and sits above
        // the players.
        tia.ResolveVideoOutput(player0: true, player1: false, playfield: true, ball: false, pastScreenCentre: false);
        await Assert.That(tia.Col).IsEqualTo((byte)HuePf);
        tia.ResolveVideoOutput(player0: false, player1: false, playfield: true, ball: false, pastScreenCentre: true);
        await Assert.That(tia.Col).IsEqualTo((byte)HuePf);
    }

    [Test]
    public async Task PlayfieldColourWinsOnAPlayfieldBallOverlap()
    {
        // Item the plan flags for verification: when a PF bit and the ball
        // pixel coincide (same priority group, different colours only in
        // score mode), the playfield colour is drawn. Confirmed against
        // Stella's renderPixel, which tests PF before BL in every mode.
        var tia = NewTia();
        SetColours(tia);
        Write(tia, Ctrlpf, CtrlpfScore);

        // Left half, PF bit + ball both lit, no players: PF (COLUP0) wins over
        // the ball (COLUPF).
        tia.ResolveVideoOutput(player0: false, player1: false, playfield: true, ball: true, pastScreenCentre: false);
        await Assert.That(tia.Col).IsEqualTo((byte)HueP0);

        // Right half: PF (COLUP1) still wins over the ball.
        tia.ResolveVideoOutput(player0: false, player1: false, playfield: true, ball: true, pastScreenCentre: true);
        await Assert.That(tia.Col).IsEqualTo((byte)HueP1);
    }

    [Test]
    public async Task MissileInheritsItsPlayersPrioritySlot()
    {
        // DoVideo folds each missile into its player's presence bit before
        // calling the resolver (player0 arg == P0 || M0, likewise P1), so a
        // missile is composited at exactly its player's colour and priority.
        var tia = NewTia();
        SetColours(tia);
        Write(tia, Ctrlpf, 0);

        // M0 (passed as player0) over player 1 -> COLUP0 wins.
        tia.ResolveVideoOutput(player0: true, player1: true, playfield: false, ball: false, pastScreenCentre: false);
        await Assert.That(tia.Col).IsEqualTo((byte)HueP0);

        // M1 (passed as player1) over the playfield -> COLUP1 wins, nothing
        // above P0 covers it.
        tia.ResolveVideoOutput(player0: false, player1: true, playfield: true, ball: true, pastScreenCentre: false);
        await Assert.That(tia.Col).IsEqualTo((byte)HueP1);
    }

    // --- Full-machinery: raw presence bits and the score-mode split point ---

    private static void Tick(TiaChip tia)
    {
        tia.Osc = false;
        tia.Osc = true;
    }

    [Test]
    public async Task RawPresenceBitsRecordEveryObjectRegardlessOfWhoTheResolverDrew()
    {
        // Collisions are registered from the unresolved presence bits, so
        // TiaChip.CurrentObjectPixels must report a hidden object too. Put the
        // ball directly under a fully-lit player 0 in normal priority: the
        // resolver draws player 0, but the ball's raw bit still has to be set.
        var tia = NewTia();
        Write(tia, Grp0, 0xFF);
        Write(tia, Colup0, ColorLuma(HueP0, 6));
        Write(tia, Colupf, ColorLuma(HuePf, 4));
        Write(tia, Ctrlpf, 0x30); // ball width 8, normal priority (D2 clear)
        Write(tia, Enabl, 0b10);

        // Strobe RESP0 and RESBL on the same colour clock so player copy 0 and
        // the ball start together, then let both counters settle.
        NextVisibleLineStart(tia);
        for (var i = 0; i < 40; i++)
        {
            Tick(tia);
        }

        Write(tia, Resp0, 0);
        Write(tia, Resbl, 0);

        NextVisibleLineStart(tia);
        NextVisibleLineStart(tia);
        NextVisibleLineStart(tia);

        var overlapClocks = 0;
        while (!tia.Blk)
        {
            Tick(tia);
            var px = tia.CurrentObjectPixels;

            // Playfield graphic is empty, so its raw bit is never set; the
            // other player / missiles are idle too.
            await Assert.That(px.Playfield).IsFalse();
            await Assert.That(px.Player1).IsFalse();
            await Assert.That(px.Missile0).IsFalse();
            await Assert.That(px.Missile1).IsFalse();

            if (px.Player0 && px.Ball)
            {
                overlapClocks++;

                // Resolver drew player 0 (normal priority), yet the ball's raw
                // presence bit is still exposed for collision detection.
                await Assert.That(tia.Col).IsEqualTo((byte)HueP0);
            }
        }

        // Player copy 0 (8 px) and the 8-clock ball start together, so several
        // colour clocks carry both raw bits at once.
        await Assert.That(overlapClocks).IsGreaterThan(0);
    }

    private static void NextVisibleLineStart(TiaChip tia)
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

    private static byte[] ScanLineColours(TiaChip tia)
    {
        NextVisibleLineStart(tia);

        var colours = new List<byte>();
        while (!tia.Blk)
        {
            colours.Add(tia.Col);
            Tick(tia);
        }

        return colours.ToArray();
    }

    [Test]
    [Arguments(CtrlpfScore)]                          // reflect off
    [Arguments((byte)(CtrlpfScore | CtrlpfReflect))]  // reflect on
    public async Task ScoreModeSplitLandsAtScreenCentreRegardlessOfReflect(byte ctrlpf)
    {
        var tia = NewTia();
        Write(tia, Colup0, ColorLuma(HueP0, 6));
        Write(tia, Colup1, ColorLuma(HueP1, 2));
        Write(tia, Colupf, ColorLuma(HuePf, 4));

        // Lit playfield across the line (PF1/PF2 span PF pixels 16..79 of each
        // half), so every scanned pixel in that band is score-tinted and the
        // COLUP0 -> COLUP1 handover marks the centre split. PF0 is deliberately
        // left clear: the current playfield decode drops its 4 leftmost bits
        // (they land above bit 15 of a ushort accumulator), which would only
        // add dead pixels at each half's start without moving the split.
        Write(tia, Pf1, 0xFF);
        Write(tia, Pf2, 0xFF);
        Write(tia, Ctrlpf, ctrlpf);

        // Let the playfield machinery settle, then scan a clean line.
        ScanLineColours(tia);
        var line = ScanLineColours(tia);

        var lastLeftTint = -1;
        var firstRightTint = -1;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == HueP0)
            {
                lastLeftTint = i;

                // COLUP0 tint never appears at or past the centre split.
                await Assert.That(i).IsLessThan(80);
            }
            else if (line[i] == HueP1)
            {
                if (firstRightTint < 0)
                {
                    firstRightTint = i;
                }

                // COLUP1 tint never appears before the centre split.
                await Assert.That(i).IsGreaterThanOrEqualTo(80);
            }
        }

        // Both halves actually rendered, and the left-half tint runs out
        // exactly 80 colour clocks into the visible region - the playfield's
        // 20-bit left half at 4 colour clocks per bit - whether or not
        // CTRLPF D0 (reflect) is set.
        await Assert.That(lastLeftTint).IsEqualTo(79);
        await Assert.That(firstRightTint).IsGreaterThan(lastLeftTint);
    }
}
