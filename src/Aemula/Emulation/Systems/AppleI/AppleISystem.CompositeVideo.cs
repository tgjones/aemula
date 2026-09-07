namespace Aemula.Emulation.Systems.AppleI;

// The video mixer: Q5, an NPN emitter follower, with R1 (1.5k) from ICD1's
// QH (the dot output) and R2 (3k) from ICC13's /Q2 (the registered
// composite-sync level, low during a sync pulse) summed at its base, and
// R12, the 100 ohm "video level" pot, as its emitter load - the output is
// the pot's wiper. Monochrome: there is no colour-burst gate anywhere on
// the terminal sheet.
//
// Both sources are plain TTL outputs, so the base sits at the resistor-
// weighted average (2 * video + sync) / 3 of two levels that are either
// VOL (~0.2V) or VOH (~3.4V), and the emitter follows it 0.65V lower - or
// sits at ground, held there by R12, once the base drops below that. That
// gives four levels:
//
//   sync low,  dot low  : base 0.2V  -> emitter 0     (sync tip)
//   sync high, dot low  : base 1.27V -> emitter 0.62V (blanking)
//   sync low,  dot high : base 2.33V -> emitter 1.68V
//   sync high, dot high : base 3.4V  -> emitter 2.75V (white)
//
// The pot only scales all four together, so the ratios are what the board
// fixes: blanking lands at about 22% of white, versus NTSC's 29%. The
// bytes below are those voltages with the pot turned to put white at 255;
// Television re-derives its own black and sync levels from the signal, so
// blanking sitting lower than the 64 other producers use is fine, and the
// third level - dots during sync - never actually occurs on a running
// board (ICD1 only loads inside the active window) but is there for the
// power-on transient, when the line buffer's zeroed contents still read as
// '@' glyphs until the first blanking-time load clears them.
public sealed partial class AppleISystem
{
    private const byte SyncTipByte = 0;
    private const byte DotDuringSyncByte = 156;
    private const byte BlankingByte = 57;
    private const byte WhiteByte = 255;

    private void TickCompositeVideo()
    {
        var syncBar = _icc13.Qn2;
        var dot = _icd1.Qh;

        var sample = syncBar
            ? (dot ? WhiteByte : BlankingByte)
            : (dot ? DotDuringSyncByte : SyncTipByte);

        Television.Decode(sample);
    }
}
