using Aemula.Emulation.Chips;

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
// character generator (_characterGenerator, see AppleISystem.cs) through a
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

        bool out1 = false, out2 = false, out3 = false, out4 = false, out5 = false;

        if (active)
        {
            var ringPosition = row * VisibleColumns + column;
            var code = PeekCharacterCode(ringPosition) & 0x3F;
            var scanline = VerticalCount % 8;

            _characterGenerator.Address4 = (code & 0x01) != 0;
            _characterGenerator.Address5 = (code & 0x02) != 0;
            _characterGenerator.Address6 = (code & 0x04) != 0;
            _characterGenerator.Address7 = (code & 0x08) != 0;
            _characterGenerator.Address8 = (code & 0x10) != 0;
            _characterGenerator.Address9 = (code & 0x20) != 0;
            _characterGenerator.Address1 = (scanline & 0x01) != 0;
            _characterGenerator.Address2 = (scanline & 0x02) != 0;
            _characterGenerator.Address3 = (scanline & 0x04) != 0;

            out1 = _characterGenerator.Out1 == true;
            out2 = _characterGenerator.Out2 == true;
            out3 = _characterGenerator.Out3 == true;
            out4 = _characterGenerator.Out4 == true;
            out5 = _characterGenerator.Out5 == true;
        }

        // D..H = the 2513's 5-bit glyph row (D=O1..H=O5, per the schematic
        // wiring traced in AppleISystem.CharacterMemory.cs's header); A/B/C
        // are grounded on real hardware, giving 3 dots of inter-character
        // spacing after the 5 glyph dots.
        _pixelShiftRegister.H = out5;
        _pixelShiftRegister.G = out4;
        _pixelShiftRegister.F = out3;
        _pixelShiftRegister.E = out2;
        _pixelShiftRegister.D = out1;
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
