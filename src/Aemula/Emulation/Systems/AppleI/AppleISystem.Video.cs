using Aemula.Emulation.Chips;

namespace Aemula.Emulation.Systems.AppleI;

// Text video: reads the current character code straight off the real 2519
// line buffer's OUT pins (see AppleISystem.CharacterMemory.cs.TickLineBufferClock
// for how its Recirculate/Clock pins are driven from the traced schematic
// signals LINE0/LINE7), then feeds the 2513 character generator
// (_characterGenerator, see AppleISystem.cs) through a real Ttl74166Chip
// (ICD1) to produce one video dot per call.
public sealed partial class AppleISystem
{
    private readonly Ttl74166Chip _pixelShiftRegister = new();

    // 40x24. Once the cursor is pushed past the last row, the next committed
    // write reloads the ICD8/ICD9 vertical counter mid-blank (see
    // AppleISystem.VideoTiming.cs), sliding the display up one row.
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
            var scanline = GlyphRow;

            _characterGenerator.Address4 = _lineBuffer.Out1;
            _characterGenerator.Address5 = _lineBuffer.Out2;
            _characterGenerator.Address6 = _lineBuffer.Out3;
            _characterGenerator.Address7 = _lineBuffer.Out4;
            _characterGenerator.Address8 = _lineBuffer.Out5;

            // The RD7 bit-plane reaches the line buffer through ICC10:A, a
            // NOR (see AppleISystem.CharacterMemory.cs), so Out6 is that code
            // bit inverted. The Apple I's own 2513 is masked to match; the
            // shared ROM image isn't, so flip it back here.
            _characterGenerator.Address9 = !_lineBuffer.Out6;
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
