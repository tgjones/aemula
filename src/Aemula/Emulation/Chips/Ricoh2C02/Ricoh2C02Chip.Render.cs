namespace Aemula.Emulation.Chips.Ricoh2C02;

// Background-only render path (no sprites, no mid-frame scroll). Follows
// https://www.nesdev.org/wiki/PPU_rendering and https://www.nesdev.org/wiki/PPU_scrolling.
partial class Ricoh2C02Chip
{
    // Two 16-bit pattern-table shift registers.
    private ushort _bgShifterPatternLo;
    private ushort _bgShifterPatternHi;

    // Two 8-bit attribute shift registers, each fed one bit per dot from a 1-bit latch
    // that is reloaded with the current tile's palette-group bits every 8 dots.
    private byte _bgShifterAttribLo;
    private byte _bgShifterAttribHi;
    private byte _bgAttribLatchLo;
    private byte _bgAttribLatchHi;

    // Bytes latched while the current 8-dot group fetches its tile; consumed by the
    // shift-register reload at the start of the following group.
    private byte _bgTileId;
    private byte _bgAttribute;
    private byte _bgPatternLo;
    private byte _bgPatternHi;
    private ushort _bgPatternBaseAddress;

    /// <summary>
    /// The 6-bit NES palette colour ($00-$3F) selected for the current dot's background
    /// pixel. Holds the pixel-mux result during active picture, and the $3F00 backdrop
    /// colour otherwise. Read by the video-signal state machine to pick a DAC tap set.
    /// </summary>
    internal byte CurrentBackgroundColor;

    /// <summary>
    /// True when the current dot lies inside the visible 256x240 picture area (visible
    /// scanline 0-239, dots 1-256). Used by the video-signal state machine to switch
    /// between active-video output and blanking / sync.
    /// </summary>
    internal bool IsRenderingActivePicture;

    private void RenderTick()
    {
        var renderingEnabled = MaskRegister.RenderBackground || MaskRegister.RenderSprites;
        var visibleScanline = CurrentScanline <= 239;
        var preRenderScanline = CurrentScanline == 261;

        if (renderingEnabled && (visibleScanline || preRenderScanline))
        {
            // Advance the pattern / attribute shifters one dot.
            if ((CurrentDot >= 2 && CurrentDot <= 257) || (CurrentDot >= 322 && CurrentDot <= 337))
            {
                _bgShifterPatternLo <<= 1;
                _bgShifterPatternHi <<= 1;
                _bgShifterAttribLo = (byte)((_bgShifterAttribLo << 1) | _bgAttribLatchLo);
                _bgShifterAttribHi = (byte)((_bgShifterAttribHi << 1) | _bgAttribLatchHi);
            }

            BackgroundFetchTick();

            // Scroll-address updates. Only reached with rendering enabled.
            if (CurrentDot == 256)
            {
                IncrementScrollVertical();
            }
            else if (CurrentDot == 257)
            {
                // Copy the horizontal bits (coarse X + nametable X) of t into v.
                _v = (ushort)((_v & 0x7BE0) | (_t & 0x041F));
            }

            if (preRenderScanline && CurrentDot >= 280 && CurrentDot <= 304)
            {
                // Copy the vertical bits (fine Y + coarse Y + nametable Y) of t into v.
                _v = (ushort)((_v & 0x041F) | (_t & 0x7BE0));
            }
        }

        // Background pixel mux for the visible picture area.
        if (visibleScanline && CurrentDot >= 1 && CurrentDot <= 256)
        {
            IsRenderingActivePicture = true;
            CurrentBackgroundColor = ComputeBackgroundColor();
        }
        else
        {
            IsRenderingActivePicture = false;
            CurrentBackgroundColor = ReadPaletteMemory(0x3F00);
        }
    }

    // Kept separate so sprite evaluation and sprite-pattern fetches can be dropped in
    // alongside this later without disturbing the background timing.
    private void BackgroundFetchTick()
    {
        // Reload the shifters from the tile fetched during the previous 8-dot group.
        // Dots 9, 17, ..., 257 for the visible fetches, plus 329 and 337 for the two
        // prefetch tiles at the end of the line.
        var reloadDot = (CurrentDot >= 9 && CurrentDot <= 257) || CurrentDot == 329 || CurrentDot == 337;
        if (reloadDot && CurrentDot % 8 == 1)
        {
            // The previous group's pattern-table high byte is on the data bus this dot.
            _bgPatternHi = _adBus.Data;

            _bgShifterPatternLo = (ushort)((_bgShifterPatternLo & 0xFF00) | _bgPatternLo);
            _bgShifterPatternHi = (ushort)((_bgShifterPatternHi & 0xFF00) | _bgPatternHi);
            _bgAttribLatchLo = (byte)(_bgAttribute & 0x01);
            _bgAttribLatchHi = (byte)((_bgAttribute >> 1) & 0x01);
        }

        var visibleFetch = CurrentDot >= 1 && CurrentDot <= 256;
        var prefetch = CurrentDot >= 321 && CurrentDot <= 336;
        if (!visibleFetch && !prefetch)
        {
            return;
        }

        // Repeating 8-dot fetch: nametable (1-2), attribute (3-4), pattern low (5-6),
        // pattern high (7-8). Each fetch drives the multiplexed PPU address/data pins the
        // same way the CPU VRAM-request handshake does: the first dot sets the address and
        // raises ALE; the second dot drops /RD so NesSystem.DoPpuCycle drives the byte back
        // onto PpuAddressData.Data. That result is latched on the first dot of the next
        // fetch (and the pattern-high byte on the reload dot above).
        switch ((CurrentDot - 1) % 8)
        {
            case 0: // Nametable byte - address.
                BeginVramFetch((ushort)(0x2000 | (_v & 0x0FFF)));
                break;

            case 1: // Nametable byte - data.
                EndVramFetch();
                break;

            case 2: // Attribute byte - address.
                _bgTileId = _adBus.Data;
                BeginVramFetch((ushort)(
                    0x23C0 | (_v & 0x0C00) | ((_v >> 4) & 0x38) | ((_v >> 2) & 0x07)));
                break;

            case 3: // Attribute byte - data.
                EndVramFetch();
                break;

            case 4: // Pattern-table low byte - address.
            {
                var attribute = _adBus.Data;
                if ((_v & 0x0040) != 0) // coarse Y bit 1 -> bottom half of the 32x32 block
                {
                    attribute >>= 4;
                }
                if ((_v & 0x0002) != 0) // coarse X bit 1 -> right half of the 32x32 block
                {
                    attribute >>= 2;
                }
                _bgAttribute = (byte)(attribute & 0x03);

                _bgPatternBaseAddress = (ushort)(
                    (CtrlRegister.BackgroundPatternTableAddress << 12)
                    | (_bgTileId << 4)
                    | ((_v >> 12) & 7)); // fine Y
                BeginVramFetch(_bgPatternBaseAddress);
                break;
            }

            case 5: // Pattern-table low byte - data.
                EndVramFetch();
                break;

            case 6: // Pattern-table high byte - address.
                _bgPatternLo = _adBus.Data;
                BeginVramFetch((ushort)(_bgPatternBaseAddress + 8));
                break;

            case 7: // Pattern-table high byte - data.
                EndVramFetch();
                IncrementScrollHorizontal();
                break;
        }
    }

    private byte ComputeBackgroundColor()
    {
        var screenX = CurrentDot - 1;

        if (!MaskRegister.RenderBackground ||
            (screenX < 8 && !MaskRegister.RenderBackgroundLeft))
        {
            // Background not shown for this pixel: the $3F00 backdrop colour shows through.
            return ReadPaletteMemory(0x3F00);
        }

        var patternMux = (ushort)(0x8000 >> _x);
        var patternBits =
            ((_bgShifterPatternHi & patternMux) != 0 ? 2 : 0) |
            ((_bgShifterPatternLo & patternMux) != 0 ? 1 : 0);

        if (patternBits == 0)
        {
            // Transparent background pixel -> universal backdrop at $3F00.
            return ReadPaletteMemory(0x3F00);
        }

        var attribMux = (byte)(0x80 >> _x);
        var attribBits =
            ((_bgShifterAttribHi & attribMux) != 0 ? 2 : 0) |
            ((_bgShifterAttribLo & attribMux) != 0 ? 1 : 0);

        var paletteIndex = (attribBits << 2) | patternBits;

        // Colour emphasis ($2001 bits 5-7) is deliberately not modelled: this is where the
        // ~120-degree-wide chroma pull-down for the emphasised sub-bands would be applied
        // to the colour before it reaches the video DAC.
        return ReadPaletteMemory((ushort)(0x3F00 | paletteIndex));
    }

    private void BeginVramFetch(ushort address)
    {
        _adBus.Address = address;
        _ppuAle = true;
        _ppuRd = true;
        _ppuWr = true;
    }

    private void EndVramFetch()
    {
        _ppuAle = false;
        _ppuRd = false;
        _ppuWr = true;
    }

    private void IncrementScrollHorizontal()
    {
        if ((_v & 0x001F) == 31)
        {
            _v &= 0xFFE0;   // coarse X = 0
            _v ^= 0x0400;   // switch horizontal nametable
        }
        else
        {
            _v++;
        }
    }

    private void IncrementScrollVertical()
    {
        if ((_v & 0x7000) != 0x7000)
        {
            _v += 0x1000;   // fine Y < 7: increment fine Y
        }
        else
        {
            _v &= 0x0FFF;   // fine Y = 0
            var coarseY = (_v & 0x03E0) >> 5;
            if (coarseY == 29)
            {
                coarseY = 0;
                _v ^= 0x0800;   // switch vertical nametable
            }
            else if (coarseY == 31)
            {
                coarseY = 0;    // out of the attribute area; do not switch nametable
            }
            else
            {
                coarseY++;
            }
            _v = (ushort)((_v & 0x7C1F) | (coarseY << 5));
        }
    }
}
