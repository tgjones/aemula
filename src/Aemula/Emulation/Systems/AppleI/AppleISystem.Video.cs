using Aemula.Emulation.Chips;
using Aemula.Emulation.Systems.AppleI.Roms;

namespace Aemula.Emulation.Systems.AppleI;

// Text video: reads the current screen position's character code straight
// out of the character-memory rings (see AppleISystem.CharacterMemory.cs -
// this is the one deliberate fidelity gap in this phase: real hardware can
// only ever see "whichever bit is at OUT right now", so it needs the 2519
// line buffer to hold one row static across the 8 scanlines that redraw it;
// working out the real relationship between the 2519's own clock and the
// character rings' clock - so that the 2519 is being reloaded on exactly
// the right scanline and otherwise recirculating its own held content -
// wasn't resolved from the rendered schematic tiles in the time available.
// Peek(ringPosition) sidesteps that puzzle by reading the character rings'
// storage directly instead of through the 2519's serial output. The write
// side (AppleISystem.CharacterMemory.cs) does not take this shortcut - it
// only ever goes through the real chips' In/Out pins), then feeds the 2513
// character generator's glyph data (Roms/CharacterGenerator.cs) through a
// real Ttl74166Chip (ICD1) to produce one video dot per call.
public sealed partial class AppleISystem
{
    private readonly Ttl74166Chip _pixelShiftRegister = new();

    // 40x24 - no scroll yet (see
    // AppleISystem.CharacterMemory.cs's _ringPosition remarks): once the
    // cursor passes the last position it wraps back to the top-left rather
    // than scrolling the screen up.
    private const int VisibleColumns = 40;
    private const int VisibleRows = 24;

    public bool VideoBit { get; private set; }

    // Loads the next character's glyph row - called once per character-time
    // (the same characterRateRisingEdge hook DoCpuMemoryAccess uses), before
    // TickCharacterMemory advances the ring, so this reads the position that
    // was current for the character-time that's ending.
    private void TickVideo()
    {
        var row = VerticalCount / 8;
        var column = HorizontalCount - HorizontalActiveStart;
        var active = column is >= 0 and < VisibleColumns && row < VisibleRows;

        byte glyphRow = 0;

        if (active)
        {
            var ringPosition = row * VisibleColumns + column;
            var code = PeekCharacterCode(ringPosition) & 0x3F;
            var scanline = VerticalCount % 8;
            glyphRow = CharacterGenerator.Image[code * 8 + scanline];
        }

        // D..H = the 2513's 5-bit glyph row (D=O1..H=O5, per the schematic
        // wiring traced in AppleISystem.CharacterMemory.cs's header); A/B/C
        // are grounded on real hardware, giving 3 dots of inter-character
        // spacing after the 5 glyph dots.
        _pixelShiftRegister.H = (glyphRow & 0x10) != 0;
        _pixelShiftRegister.G = (glyphRow & 0x08) != 0;
        _pixelShiftRegister.F = (glyphRow & 0x04) != 0;
        _pixelShiftRegister.E = (glyphRow & 0x02) != 0;
        _pixelShiftRegister.D = (glyphRow & 0x01) != 0;
        _pixelShiftRegister.C = false;
        _pixelShiftRegister.B = false;
        _pixelShiftRegister.A = false;
        _pixelShiftRegister.Ser = false;
        _pixelShiftRegister.ClkInh = false;

        _pixelShiftRegister.ShLd = false;
        PulsePixelShiftClock();
        _pixelShiftRegister.ShLd = true;
    }

    // Called every master tick (dot rate is master/2 - a 14-master-tick
    // character cell is 7 dots wide, matching the 5 glyph dots plus 2 of
    // the 3 blanked shift positions; the last is never sampled, same as
    // Apple II's equivalent shift-out). Reflects the video bit for
    // whichever dot is current; AppleISystem.CompositeVideo.cs samples
    // VideoBit once per master tick, same shape as AppleII's
    // _videoDataBits.
    private void TickVideoDot()
    {
        if (_dotDivider % 2 == 0 && _dotDivider > 0)
        {
            PulsePixelShiftClock();
        }

        VideoBit = HSync || VSync ? false : _pixelShiftRegister.Qh;
    }

    private void PulsePixelShiftClock()
    {
        _pixelShiftRegister.Clk = false;
        _pixelShiftRegister.Clk = true;
    }
}
