using System.Threading.Tasks;
using Aemula.Emulation.Chips.Tia;

namespace Aemula.Tests.Emulation.Chips.Tia;

// Drives TiaChip.ResolveVideoOutput directly - the single priority stage
// that replaced the old "playfield, then P0, then P1 each overwrite Lum/Col
// in turn" scheme (which gave P1 priority over P0, backwards). The resolver
// only reads register state, so these tests set that state via real Phi2
// register writes and then call the resolver with hand-picked object
// presence bits, rather than positioning objects pixel-exact through the
// full horizontal-counter machinery.
public class TiaCompositingTests
{
    // TIA write-register addresses touched by these tests.
    private const byte Colup0 = 0x06;
    private const byte Colup1 = 0x07;
    private const byte Colupf = 0x08;
    private const byte Colubk = 0x09;
    private const byte Ctrlpf = 0x0A;

    // CTRLPF bits.
    private const byte CtrlpfScore = 0b0000_0010;    // D1
    private const byte CtrlpfPriority = 0b0000_0100; // D2

    private static TiaChip NewTia()
    {
        // CS0/CS2/CS3 are active-low and default to false (asserted); CS1 is
        // active-high, so it is the only chip-select pin that has to be
        // driven for the chip to treat a Phi2 edge as a register access.
        return new TiaChip { CS1 = true };
    }

    private static void Write(TiaChip tia, byte address, byte data)
    {
        tia.RW = false;
        tia.Address = address;
        tia.Data05 = (byte)(data & 0x3F);
        tia.Data67 = (byte)(data >> 6);
        tia.Phi2 = false;
        tia.Phi2 = true;
    }

    // hue<<4 | luma<<1 - TIA's COLUPx/COLUPF/COLUBK byte layout. Read back
    // through the chip, Col is the hue and Lum is the luma.
    private static byte ColorLuma(int hue, int luma) => (byte)((hue << 4) | (luma << 1));

    [Test]
    public async Task Player0BeatsPlayer1OnOverlap()
    {
        var tia = NewTia();
        Write(tia, Colup0, ColorLuma(3, 6));
        Write(tia, Colup1, ColorLuma(9, 2));

        tia.ResolveVideoOutput(player0: true, player1: true, playfield: false, ball: false, pastScreenCentre: false);

        await Assert.That(tia.Col).IsEqualTo((byte)3);
        await Assert.That(tia.Lum).IsEqualTo((byte)6);
    }

    [Test]
    public async Task PlayersBeatPlayfieldInNormalPriority()
    {
        var tia = NewTia();
        Write(tia, Colup1, ColorLuma(9, 2));
        Write(tia, Colupf, ColorLuma(4, 4));

        tia.ResolveVideoOutput(player0: false, player1: true, playfield: true, ball: false, pastScreenCentre: false);

        await Assert.That(tia.Col).IsEqualTo((byte)9);
        await Assert.That(tia.Lum).IsEqualTo((byte)2);
    }

    [Test]
    public async Task PlayfieldFallsBackToBackgroundWhenNoObjectPresent()
    {
        var tia = NewTia();
        Write(tia, Colubk, ColorLuma(7, 1));

        tia.ResolveVideoOutput(player0: false, player1: false, playfield: false, ball: false, pastScreenCentre: false);

        await Assert.That(tia.Col).IsEqualTo((byte)7);
        await Assert.That(tia.Lum).IsEqualTo((byte)1);
    }

    [Test]
    public async Task PlayfieldPriorityBitPutsPlayfieldAbovePlayers()
    {
        var tia = NewTia();
        Write(tia, Colup0, ColorLuma(3, 6));
        Write(tia, Colupf, ColorLuma(4, 4));

        Write(tia, Ctrlpf, CtrlpfPriority);
        tia.ResolveVideoOutput(player0: true, player1: false, playfield: true, ball: false, pastScreenCentre: false);
        await Assert.That(tia.Col).IsEqualTo((byte)4);
        await Assert.That(tia.Lum).IsEqualTo((byte)4);

        // Same object bits, priority bit cleared: the player wins again.
        Write(tia, Ctrlpf, 0);
        tia.ResolveVideoOutput(player0: true, player1: false, playfield: true, ball: false, pastScreenCentre: false);
        await Assert.That(tia.Col).IsEqualTo((byte)3);
        await Assert.That(tia.Lum).IsEqualTo((byte)6);
    }

    [Test]
    public async Task PlayfieldPriorityStillLetsPlayersBeatBackground()
    {
        var tia = NewTia();
        Write(tia, Colup1, ColorLuma(9, 2));
        Write(tia, Colubk, ColorLuma(7, 1));
        Write(tia, Ctrlpf, CtrlpfPriority);

        tia.ResolveVideoOutput(player0: false, player1: true, playfield: false, ball: false, pastScreenCentre: false);

        await Assert.That(tia.Col).IsEqualTo((byte)9);
        await Assert.That(tia.Lum).IsEqualTo((byte)2);
    }

    [Test]
    public async Task ScoreModeTintsPlayfieldWithPerHalfPlayerColour()
    {
        var tia = NewTia();
        Write(tia, Colup0, ColorLuma(3, 6));
        Write(tia, Colup1, ColorLuma(9, 2));
        Write(tia, Colupf, ColorLuma(4, 4));
        Write(tia, Ctrlpf, CtrlpfScore);

        // Left half of the screen (not past centre) -> COLUP0.
        tia.ResolveVideoOutput(player0: false, player1: false, playfield: true, ball: false, pastScreenCentre: false);
        await Assert.That(tia.Col).IsEqualTo((byte)3);
        await Assert.That(tia.Lum).IsEqualTo((byte)6);

        // Right half (past centre) -> COLUP1.
        tia.ResolveVideoOutput(player0: false, player1: false, playfield: true, ball: false, pastScreenCentre: true);
        await Assert.That(tia.Col).IsEqualTo((byte)9);
        await Assert.That(tia.Lum).IsEqualTo((byte)2);
    }

    [Test]
    public async Task PlayfieldPriorityOverridesScoreMode()
    {
        var tia = NewTia();
        Write(tia, Colup0, ColorLuma(3, 6));
        Write(tia, Colupf, ColorLuma(4, 4));
        Write(tia, Ctrlpf, (byte)(CtrlpfScore | CtrlpfPriority));

        tia.ResolveVideoOutput(player0: false, player1: false, playfield: true, ball: false, pastScreenCentre: false);

        // Priority wins: the playfield keeps COLUPF, not COLUP0.
        await Assert.That(tia.Col).IsEqualTo((byte)4);
        await Assert.That(tia.Lum).IsEqualTo((byte)4);
    }

    [Test]
    public async Task PlayerPixelIsOffWhenNoGraphicScheduled()
    {
        // The new PlayerAndMissile.DoPlayer() (no TiaChip argument) reports
        // its pixel via PixelOn instead of writing the video output. With no
        // graphics scan armed, the pixel stays off however full GRPx is.
        var player = new PlayerAndMissile { Graphics = 0xFF };

        player.DoPlayer();

        await Assert.That(player.PixelOn).IsFalse();
    }
}
