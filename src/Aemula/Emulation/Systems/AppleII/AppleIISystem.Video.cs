using Aemula.Emulation.Chips;

namespace Aemula.Emulation.Systems.AppleII;

// TEXT/LORES video address generation, character-ROM lookup, and
// NORMAL/INVERSE/FLASH, driven off the video scanner. Modelled from
// Jim Sather's "Understanding the Apple II", chapter 5 ("RAM in the Apple
// II", "The Arithmetic of Video Scanner Memory Addressing") and chapter 8
// ("Video Generation"), cross-checked against AppleWin's Video.cpp.
//
// And the $C050-$C057 screen mode soft switches, LORES color block
// generation, and HIRES addressing/shifting, plus PAGE2 for both. Also
// modelled from Sather chapters 5, 7 ("Address Decoding and Input/Output"),
// and 8, cross-checked against AppleWin's VideoGetScannerAddress.
public sealed partial class AppleIISystem
{
    // The address-scrambling adder (Sather's "D9"): folds H5-H4-H3 and a
    // V4/V3-selected per-third offset into SUM-A3..SUM-A6. Shared verbatim
    // by TEXT, LORES, and HIRES addressing (Sather: "HIRES video scanner
    // addressing is identical to TEXT/LORES addressing on bits A0-A9").
    private readonly Ttl74283Chip _videoAddressAdder;

    // The $C050-$C057 screen mode soft switches (and, sharing the same
    // physical chip, the $C058-$C05F annunciators - not consumed until a
    // later phase). Sather ch. 7: "The $C05X range is broken down into
    // eight off/on soft switches by the LS259 at F14." CPU A1-A3 select
    // which of the 8 latches; A0 is the D input (low address of a pair =
    // off, high = on), matching the GRAPHICS/TEXT, NOMIX/MIX, PAGE1/PAGE2,
    // LORES/HIRES ordering of Table 7.1. Enabled (G) by the address decoder
    // chain's F13/_ioControlDecoder.Y5 - see AppleIISystem.cs.
    private readonly Ttl74259Chip _modeSwitchLatch;

    // GRAPHICS mode still shows TEXT for the bottom four text rows once
    // MixMode is set (Sather's "HIRES TIME"/"GRAPHICS TIME" terms).
    private bool TextMode => _modeSwitchLatch.Q0;
    private bool MixMode => _modeSwitchLatch.Q1;
    private bool Page2Mode => _modeSwitchLatch.Q2;
    private bool HiresMode => _modeSwitchLatch.Q3;

    // V4.V2 identifies scan lines 160-191 (and 224-261, during VBL) - the
    // bottom four text rows of a mixed display (Sather p.5-14). Real
    // hardware delays this switch by three RAS' pulses so the last HIRES
    // dots of line 159 finish shifting out first; since our video-scanner
    // access and pixel-draw for a line happen together in one step (the
    // same "collapsed" simplification already used for LDPS' and the shift
    // register load), that delay has no visible effect here and is skipped.
    private bool ShowText => TextMode || (MixMode && V4 && V2);
    private bool ShowHires => !ShowText && HiresMode;

    // Continuously wired from the CPU address bus on every access, the same
    // way SetHighMemoryDecoderAddress wires the other decode chips - no
    // "is this address relevant" branch of its own. G is disabled first so
    // the address-bit updates below can't glitch a stale selection through
    // while G is still asserted from whatever the previous access was; only
    // the final G assignment (from the real decoder output) can commit a
    // write, and only when this access's address actually decodes to
    // $C050-$C05F.
    internal (bool TextMode, bool MixMode, bool Page2Mode, bool HiresMode) GetModeSwitchesForTests()
    {
        return (TextMode, MixMode, Page2Mode, HiresMode);
    }

    private void SetModeSwitchLatchAddress(ushort address)
    {
        _modeSwitchLatch.G = true;

        var select = (address >> 1) & 0x7;

        _modeSwitchLatch.D = (address & 1) != 0;
        _modeSwitchLatch.A0 = (select & 1) != 0;
        _modeSwitchLatch.A1 = (select & 2) != 0;
        _modeSwitchLatch.A2 = (select & 4) != 0;
        _modeSwitchLatch.G = _ioControlDecoder.Y5;
    }

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

    // A second, physically distinct 74166 for HIRES (Sather: "There are two
    // shift registers, one for GRAPHICS and one for TEXT"). Shifts the
    // low 7 bits of a HIRES byte out bit 0 first (AppleWin's
    // updatePixels(): "bits & 1" then "bits >>= 1", i.e. bit 0 is the
    // leftmost of the 7 dots).
    private readonly Ttl74166Chip _hiresVideoShiftRegister;

    // Software bookkeeping for the once-per-frame TEXT flash counter.
    private bool _wasInVblAtLastScanline;
    private int _textFlashFrameCounter;

    // The real digital PICTURE/VIDEO DATA line for the 14 master ticks of
    // whichever cell TickVideo() just scanned - one entry per master tick,
    // not per dot. TEXT/HIRES
    // shift once per dot (7M, i.e. every 2 master ticks), so those two
    // just write the same per-dot bit into both of a dot's tick slots; but
    // LORES's circulating 4-bit shift register genuinely is clocked at the
    // full 14M rate (Sather p.8-23: "circulates as clocked by 14M, the
    // sections circulate 3.5 times per video cycle" - 14 ticks / 4 bits =
    // 3.5), so it needs real per-tick resolution, not per-dot - see
    // DrawLoresByte. Forced all-false during HBL/VBL, matching Gayler's
    // "A9" blanking-gated video-data selector.
    private readonly bool[] _videoDataBits = new bool[14];

    internal bool[] GetVideoDataBitsForTests() => _videoDataBits;

    private void TickVideo()
    {
        if (!HpeBar)
        {
            // HPE' asserted: a new scanline is starting.
            if (Vbl && !_wasInVblAtLastScanline)
            {
                _textFlashFrameCounter++;
            }

            _wasInVblAtLastScanline = Vbl;
        }

        // TEXT/LORES/HIRES video address (Sather p.5-8/5-9, p.5-13): A0-A2 =
        // H0-H2; A3-A6 = the adder's SUM, adding H5-H4-H3 (with H5 wired in
        // twice, inverted, to fold in a hardwired -4) to a V4/V3-selected
        // offset, with the adder's carry-in providing the remaining +1 of
        // that -3 trick; A7-A9 = V0-V2. Identical for all three modes.
        _videoAddressAdder.A1 = H3;
        _videoAddressAdder.A2 = H4;
        _videoAddressAdder.A3 = !H5;
        _videoAddressAdder.A4 = !H5;
        _videoAddressAdder.B1 = V3;
        _videoAddressAdder.B2 = V4;
        _videoAddressAdder.B3 = V3;
        _videoAddressAdder.B4 = V4;
        _videoAddressAdder.C0 = true;

        var lowAddress =
            (H0 ? 1 << 0 : 0) |
            (H1 ? 1 << 1 : 0) |
            (H2 ? 1 << 2 : 0) |
            (_videoAddressAdder.S1 ? 1 << 3 : 0) |
            (_videoAddressAdder.S2 ? 1 << 4 : 0) |
            (_videoAddressAdder.S3 ? 1 << 5 : 0) |
            (_videoAddressAdder.S4 ? 1 << 6 : 0) |
            (V0 ? 1 << 7 : 0) |
            (V1 ? 1 << 8 : 0) |
            (V2 ? 1 << 9 : 0);

        var showHires = ShowHires;

        // A10-A14 (Sather p.5-13, "HIRES Scanning"): TEXT/LORES puts PAGE1/
        // PAGE2 alone at A10/A11 (one bit or the other, never both). HIRES
        // instead folds VA-VC into A10-A12 (eight times as much memory - 40
        // bytes per scan line instead of per 8-line row) and moves PAGE1/
        // PAGE2 up to A13/A14, giving base addresses $2000/$4000.
        var highAddress = showHires
            ? (VA ? 1 << 10 : 0) | (VB ? 1 << 11 : 0) | (VC ? 1 << 12 : 0) |
              (Page2Mode ? 1 << 14 : 1 << 13)
            : Page2Mode ? 1 << 11 : 1 << 10;

        var address = (ushort)(lowAddress | highAddress);

        // Bypasses the CPU's memory decode chips and reads the RAM array
        // directly, the same accepted fidelity exception for bulk storage
        // the CPU's own RAM access already uses. Fetched
        // unconditionally, including during HBL/VBL, since this access is
        // what the DRAM refresh side effect rides on.
        var screenByte = _ram[address];

        if (Hbl || Vbl)
        {
            for (var i = 0; i < _videoDataBits.Length; i++)
            {
                _videoDataBits[i] = false;
            }

            return;
        }

        if (showHires)
        {
            DrawHiresByte(screenByte);
            return;
        }

        if (!ShowText)
        {
            DrawLoresByte(screenByte);
            return;
        }

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
        _characterGenerator.Address4 = (screenByte & 0x01) != 0;
        _characterGenerator.Address5 = (screenByte & 0x02) != 0;
        _characterGenerator.Address6 = (screenByte & 0x04) != 0;
        _characterGenerator.Address7 = (screenByte & 0x08) != 0;
        _characterGenerator.Address8 = (screenByte & 0x10) != 0;
        _characterGenerator.Address9 = (screenByte & 0x20) != 0;
        _characterGenerator.Address1 = VA;
        _characterGenerator.Address2 = VB;
        _characterGenerator.Address3 = VC;

        // Parallel-load the ROM's dot bits, MSB first - Sather p.8-30 is
        // explicit that the TEXT ROM shifts out most-significant bit first.
        // Real hardware spreads this load-then-shift across the 7 dot
        // clocks of one character time; collapsed into one step here, the
        // same simplification already used for LDPS' above (it isn't
        // visible in the rendered raster, only in internal signal timing).
        // A and H are grounded on real hardware (the 2513 only drives 5 of
        // the cell's 7 dot positions - see Signetics2513Chip), giving a
        // blank spacer column on each side of the glyph.
        _textVideoShiftRegister.H = false;
        _textVideoShiftRegister.G = _characterGenerator.Out5 == true;
        _textVideoShiftRegister.F = _characterGenerator.Out4 == true;
        _textVideoShiftRegister.E = _characterGenerator.Out3 == true;
        _textVideoShiftRegister.D = _characterGenerator.Out2 == true;
        _textVideoShiftRegister.C = _characterGenerator.Out1 == true;
        _textVideoShiftRegister.B = false;
        _textVideoShiftRegister.A = false;
        _textVideoShiftRegister.ShLd = false;
        PulseShiftRegister();

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

            var lit = _textVideoXor.Y1;
            _videoDataBits[dot * 2] = lit;
            _videoDataBits[dot * 2 + 1] = lit;
        }
    }

    // LORES color block (Sather p.8-8): the low nibble colors the upper
    // half of an 8-scanline text row (VC low), the high nibble the lower
    // half (VC high) - the same nibble for all four scanlines of that half,
    // which is what makes it look like a solid color block instead of a
    // dot pattern.
    private void DrawLoresByte(byte screenByte)
    {
        var nibble = VC ? screenByte >> 4 : screenByte & 0xF;

        // LORES's real VIDEO DATA line isn't "direct color" - like HIRES,
        // it's a genuine bit stream, just a periodic one: Sather p.8-23
        // ("LORES Graphics Output") describes the active nibble as loaded
        // into a 4-bit "end around" shift register clocked directly by 14M
        // - twice the rate TEXT/HIRES shift at (they clock once per dot,
        // i.e. once every 2 master ticks) - so it circulates 3.5 times
        // per 14-tick video cycle (14 ticks / 4 bits = 3.5, matching
        // Sather's own "3.5 million circulations per second - the same
        // frequency as COLOR REFERENCE" aside). That's what makes a solid
        // nibble's chroma land exactly on the subcarrier fundamental (one
        // full 4-bit rotation every 4 master ticks) instead of half of it -
        // get this rate wrong and a decoder's comb filter/PLL (built around
        // exactly 4 samples/cycle) sees a period-8 signal it can't cancel,
        // which is what full per-sample luma/chroma noise inside an
        // otherwise solid LORES block turned out to be, in practice.
        //
        // Sather is also explicit about *which* bit starts the rotation,
        // not just the rate: "either its least significant bit (Q0) or its
        // third least significant bit (Q2) is clocked to the picture
        // flip-flop. Q0 is selected in video cycles where H0 was latched
        // low (even memory addresses), and Q2 is selected... high (odd
        // memory addresses)" - independently confirmed against Sather's own
        // worked example (nibble 1001 on an even cycle: "10011001100110",
        // beginning at Q0; on an odd cycle: "01100110011001", beginning at
        // Q2) - both cases rotate Q0->Q1->Q2->Q3->Q0..., only the starting
        // bit differs. H0 is the even/odd-address signal.
        var startBit = H0 ? 2 : 0;

        for (var tick = 0; tick < 14; tick++)
        {
            var bitIndex = (tick + startBit) & 3;
            _videoDataBits[tick] = ((nibble >> bitIndex) & 1) != 0;
        }
    }

    // HIRES dot pattern (Sather p.8-8): the low seven bits control seven
    // dot positions, shifted out bit 0 first. Bit 7 (DL7) doesn't affect
    // which dots are lit (on real hardware it delays the whole dot stream
    // by half a dot, which isn't modelled).
    private void DrawHiresByte(byte screenByte)
    {
        _hiresVideoShiftRegister.H = (screenByte & 0x01) != 0;
        _hiresVideoShiftRegister.G = (screenByte & 0x02) != 0;
        _hiresVideoShiftRegister.F = (screenByte & 0x04) != 0;
        _hiresVideoShiftRegister.E = (screenByte & 0x08) != 0;
        _hiresVideoShiftRegister.D = (screenByte & 0x10) != 0;
        _hiresVideoShiftRegister.C = (screenByte & 0x20) != 0;
        _hiresVideoShiftRegister.B = (screenByte & 0x40) != 0;
        _hiresVideoShiftRegister.A = false;
        _hiresVideoShiftRegister.ShLd = false;
        PulseHiresShiftRegister();

        for (var dot = 0; dot < 7; dot++)
        {
            if (dot > 0)
            {
                _hiresVideoShiftRegister.ShLd = true;
                PulseHiresShiftRegister();
            }

            var lit = _hiresVideoShiftRegister.Qh;
            _videoDataBits[dot * 2] = lit;
            _videoDataBits[dot * 2 + 1] = lit;
        }
    }

    private void PulseShiftRegister()
    {
        _textVideoShiftRegister.Clk = false;
        _textVideoShiftRegister.Clk = true;
    }

    private void PulseHiresShiftRegister()
    {
        _hiresVideoShiftRegister.Clk = false;
        _hiresVideoShiftRegister.Clk = true;
    }
}
