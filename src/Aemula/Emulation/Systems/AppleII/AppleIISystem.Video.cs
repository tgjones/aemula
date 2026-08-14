using Aemula.Emulation.Chips;

namespace Aemula.Emulation.Systems.AppleII;

// Phase 4: TEXT/LORES video address generation, character-ROM lookup, and
// NORMAL/INVERSE/FLASH, driven off the phase-3 video scanner. Modelled from
// Jim Sather's "Understanding the Apple II", chapter 5 ("RAM in the Apple
// II", "The Arithmetic of Video Scanner Memory Addressing") and chapter 8
// ("Video Generation"), cross-checked against AppleWin's Video.cpp.
public sealed partial class AppleIISystem
{
    // The address-scrambling adder (Sather's "D9"): folds H5-H4-H3 and a
    // V4/V3-selected per-third offset into SUM-A3..SUM-A6. Only page 1 text
    // is wired up (phase 4); page 2 / 80-column / hires are later phases.
    private readonly Ttl74283Chip _videoAddressAdder;

    // Shifts the character ROM's dot pattern out onto the display, MSB
    // first.
    private readonly Ttl74166Chip _textVideoShiftRegister;

    // Selectable inverter between the shift register and the display:
    // implements NORMAL/INVERSE/FLASH by XORing the raw dot pattern with
    // the latched INVERT TEXT signal.
    private readonly Ttl7486Chip _textVideoXor;

    // Latches INVERT TEXT once per character, at the same time the dot
    // pattern loads, so it can't change mid-character.
    private readonly Ttl7474Chip _invertTextLatch;

    // Software bookkeeping standing in for "which raster line of the 280x192
    // visible picture are we on" - not itself part of the real hardware,
    // since real hardware doesn't need a linear line count (the DRAM address
    // formula above only needs the raw counter bits). -1 while in VBL.
    private int _currentRasterLine = -1;
    private bool _wasInVblAtLastScanline;
    private int _textFlashFrameCounter;

    public readonly DisplayBuffer Display;

    private void TickVideo()
    {
        if (!HpeBar)
        {
            // HPE' asserted: a new scanline is starting.
            if (Vbl)
            {
                if (!_wasInVblAtLastScanline)
                {
                    _textFlashFrameCounter++;
                }

                _currentRasterLine = -1;
            }
            else
            {
                _currentRasterLine++;
            }

            _wasInVblAtLastScanline = Vbl;
        }

        // TEXT/LORES video address (Sather p.5-8/5-9): A0-A2 = H0-H2;
        // A3-A6 = the adder's SUM, adding H5-H4-H3 (with H5 wired in twice,
        // inverted, to fold in a hardwired -4) to a V4/V3-selected offset,
        // with the adder's carry-in providing the remaining +1 of that -3
        // trick; A7-A9 = V0-V2; A10 = PAGE1 (always selected here - page 2
        // is a phase 5 soft switch).
        _videoAddressAdder.A1 = H3;
        _videoAddressAdder.A2 = H4;
        _videoAddressAdder.A3 = !H5;
        _videoAddressAdder.A4 = !H5;
        _videoAddressAdder.B1 = V3;
        _videoAddressAdder.B2 = V4;
        _videoAddressAdder.B3 = V3;
        _videoAddressAdder.B4 = V4;
        _videoAddressAdder.C0 = true;

        var address = (ushort)(
            (H0 ? 1 << 0 : 0) |
            (H1 ? 1 << 1 : 0) |
            (H2 ? 1 << 2 : 0) |
            (_videoAddressAdder.S1 ? 1 << 3 : 0) |
            (_videoAddressAdder.S2 ? 1 << 4 : 0) |
            (_videoAddressAdder.S3 ? 1 << 5 : 0) |
            (_videoAddressAdder.S4 ? 1 << 6 : 0) |
            (V0 ? 1 << 7 : 0) |
            (V1 ? 1 << 8 : 0) |
            (V2 ? 1 << 9 : 0) |
            (1 << 10));

        // Bypasses the CPU's memory decode chips and reads the RAM array
        // directly - the plan's fidelity exception for bulk storage covers
        // this the same way it covers the CPU's own RAM access.
        var screenByte = _ram[address];

        // NORMAL/INVERSE/FLASH (Sather p.8-9, Fig 8.6): D7 high is always
        // NORMAL; D7 low with D6 low is INVERSE; D7 low with D6 high is
        // FLASH (alternates with the ~4Hz text flasher, approximated here
        // as toggling every 16 frames). Latched once per character, at the
        // same time the dot pattern loads.
        var d6 = (screenByte & 0x40) != 0;
        var d7 = (screenByte & 0x80) != 0;
        var flasher = (_textFlashFrameCounter & 0x10) != 0;

        _invertTextLatch.D1 = !d7 && (!d6 || flasher);
        _invertTextLatch.Clk1 = false;
        _invertTextLatch.Clk1 = true;

        // Character ROM addressing (Sather p.8-9): the low 6 bits of the
        // screen byte select one of 64 glyphs; VA-VC - the sub-scanline
        // counter, distinct from the V0-V2 already folded into the DRAM
        // address above - select which of its 8 rows.
        var charRomAddress =
            ((screenByte & 0x3F) << 3) |
            (VC ? 1 << 2 : 0) |
            (VB ? 1 << 1 : 0) |
            (VA ? 1 << 0 : 0);
        var glyphRow = _characterRom[charRomAddress];

        if (Hbl || Vbl)
        {
            return;
        }

        // Parallel-load the ROM's 7 dot bits, MSB (bit 6) first - Sather
        // p.8-30 is explicit that the TEXT ROM shifts out most-significant
        // bit first. Real hardware spreads this load-then-shift across the
        // 7 dot clocks of one character time; collapsed into one step here,
        // the same simplification already used for LDPS' above (it isn't
        // visible in the rendered raster, only in internal signal timing).
        _textVideoShiftRegister.H = (glyphRow & 0x40) != 0;
        _textVideoShiftRegister.G = (glyphRow & 0x20) != 0;
        _textVideoShiftRegister.F = (glyphRow & 0x10) != 0;
        _textVideoShiftRegister.E = (glyphRow & 0x08) != 0;
        _textVideoShiftRegister.D = (glyphRow & 0x04) != 0;
        _textVideoShiftRegister.C = (glyphRow & 0x02) != 0;
        _textVideoShiftRegister.B = (glyphRow & 0x01) != 0;
        _textVideoShiftRegister.A = false;
        _textVideoShiftRegister.ShLd = false;
        PulseShiftRegister();

        var rawH =
            (H0 ? 1 : 0) | (H1 ? 2 : 0) | (H2 ? 4 : 0) |
            (H3 ? 8 : 0) | (H4 ? 16 : 0) | (H5 ? 32 : 0);
        var baseX = (rawH - 24) * 7;

        for (var dot = 0; dot < 7; dot++)
        {
            if (dot > 0)
            {
                _textVideoShiftRegister.ShLd = true;
                PulseShiftRegister();
            }

            // The XOR gate between the shifter and the display: a
            // selectable inverter driven by the INVERT TEXT latch.
            _textVideoXor.A1 = _textVideoShiftRegister.Qh;
            _textVideoXor.B1 = _invertTextLatch.Q1;

            WritePixel(baseX + dot, _currentRasterLine, _textVideoXor.Y1);
        }
    }

    private void PulseShiftRegister()
    {
        _textVideoShiftRegister.Clk = false;
        _textVideoShiftRegister.Clk = true;
    }

    private void WritePixel(int x, int y, bool lit)
    {
        if ((uint)x >= Display.Width || (uint)y >= Display.Height)
        {
            return;
        }

        var value = lit ? (byte)0xFF : (byte)0x00;
        Display.Data[y * (int)Display.Width + x] = new RgbaByte(value, value, value, 0xFF);
    }
}
