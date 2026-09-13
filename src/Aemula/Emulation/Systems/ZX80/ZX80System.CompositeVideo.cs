namespace Aemula.Emulation.Systems.ZX80;

// IC20 gate 2 and the R30/R32 summing pair feeding Television.Decode - the
// board's only analog stage before the (out-of-scope, per the plan's
// RF-modulator fidelity note) modulator can.
public sealed partial class ZX80System
{
    private const byte SyncTipByte = 0;
    private const byte BlackByte = 57;
    private const byte WhiteByte = 255;

    private void TickCompositeVideo()
    {
        // IC20 gate 2 XORs the shift register's complemented output against
        // the inverse-video latch: for a normal (non-inverse) character,
        // that latch holds Y2 high, so VIDEO tracks QhN directly - true
        // (background/paper) whenever the font bitmap bit just shifted out
        // (Qh) is 0, false (ink/stroke) when it's 1. That's what the
        // schematic's "black text on white background" describes for the
        // normal video-polarity jumper position (see _videoShiftRegister's
        // remarks): paper is the common case, so it gets the electrically
        // "idle" (QhN high) state.
        _ic20.A2 = _videoShiftRegister.QhN;
        _ic20.B2 = _ic12.Y2;
        var video = _ic20.Y2;

        // R32 (330R, on SYNC) has roughly 3x R30's (1K, on VIDEO) pull at
        // their shared summing node, so an active sync pulse always wins
        // regardless of what the shift register is doing - the same way a
        // real composite blanking interval overrides picture content
        // outright rather than blending with it. IC19.Q1 itself idles high
        // for the whole line and dips low only for the ~20-T-state HSYNC
        // pulse (the same "low pulse" IC18's own Clr1 already keys off of
        // to catch the sync event), so the pulse this composite stage needs
        // to react to is the complement, Qn1.
        var sync = _ic19.Qn1;

        var sample = sync
            ? SyncTipByte
            : video ? WhiteByte : BlackByte;

        Television.Decode(sample);
    }
}
