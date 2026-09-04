using System;

namespace Aemula.Emulation.Chips.Ricoh2C02;

// Sprite pipeline: secondary-OAM evaluation, sprite pattern fetches, the eight
// output units (pattern shift registers + X down-counters + attribute latches)
// and, together with ComputePixelColor in Ricoh2C02Chip.Render.cs, the per-dot
// sprite pixel mux and sprite-0 hit. Sprite overflow ($2002.5) is raised here
// from the evaluation walk. SpriteTick runs once per dot from
// RenderTick, only while rendering is enabled on a visible or the pre-render
// scanline. Follows https://www.nesdev.org/wiki/PPU_sprite_evaluation and
// https://www.nesdev.org/wiki/PPU_rendering.
partial class Ricoh2C02Chip
{
    // 32 bytes = 8 candidate sprites (Y, tile, attribute, X) for the *next*
    // scanline, filled by evaluation over dots 65-256 of the current one.
    private readonly byte[] _secondaryOam = new byte[32];

    // The eight output units. Loaded by the pattern fetches at dots 257-320 and
    // consumed by the mux over dots 1-256 of the following scanline.
    private readonly byte[] _spritePatternShiftLo = new byte[8];
    private readonly byte[] _spritePatternShiftHi = new byte[8];
    private readonly byte[] _spriteAttributeLatches = new byte[8];
    private readonly byte[] _spriteXCounters = new byte[8];

    // How many of the eight units the fetch phase loaded, and whether unit 0 is
    // primary-OAM sprite 0 - both latched at dot 257 from the evaluation that
    // just finished, and read by the mux on the next scanline.
    private int _spriteCount;
    private bool _spriteZeroInRange;

    // Evaluation walk (dots 65-256): n = primary-OAM sprite (0-63), m = byte
    // within it (0-3), _secondaryOamIndex = write cursor into _secondaryOam.
    private int _evalN;
    private int _evalM;
    private int _secondaryOamIndex;
    private byte _evalLatch;
    private bool _evalDone;
    private bool _evalSpriteZeroInRange;

    // Holds a slot's pattern-low byte between its phase-6 fetch and the phase-0
    // slot load that pairs it with the pattern-high byte.
    private byte _spriteFetchLatch;

    private int SpriteHeight => CtrlRegister.SpriteSize == SpriteSize.Size8x16 ? 16 : 8;

    private void SpriteTick()
    {
        if (CurrentDot == 1)
        {
            // Secondary OAM is cleared to $FF over dots 1-64; one shot is
            // behaviourally identical for everything downstream.
            Array.Fill(_secondaryOam, (byte)0xFF);
        }
        else if (CurrentDot == 65)
        {
            _evalN = 0;
            _evalM = 0;
            _secondaryOamIndex = 0;
            _evalDone = false;
            _evalSpriteZeroInRange = false;
        }

        if (CurrentDot >= 65 && CurrentDot <= 256)
        {
            SpriteEvaluationCycle();
        }
        else if (CurrentDot >= 257 && CurrentDot <= 321)
        {
            SpritePatternFetchCycle();
        }
    }

    // One dot of the dots 65-256 evaluation walk. Odd cycles read the byte
    // primary OAM points at; even cycles copy it into secondary OAM and step the
    // n/m pointer, range-checking each sprite's Y against this scanline.
    private void SpriteEvaluationCycle()
    {
        if ((CurrentDot & 1) == 1)
        {
            // Once the walk is done the pointer sits at sprite 0 and re-reads it.
            _evalLatch = _objectAttributeMemory[((_evalN << 2) | _evalM) & 0xFF];
            return;
        }

        if (_evalDone)
        {
            return;
        }

        var secondaryFull = _secondaryOamIndex >= 32;

        if (!secondaryFull)
        {
            _secondaryOam[_secondaryOamIndex] = _evalLatch;
        }

        if (_evalM == 0)
        {
            // Handled the Y byte - is this sprite in range on this scanline?
            var row = (int)CurrentScanline - _evalLatch;
            var inRange = row >= 0 && row < SpriteHeight;

            if (secondaryFull)
            {
                // Eight sprites already found. Writes to secondary OAM are
                // disabled and the walk keeps looking for a ninth in-range
                // sprite. A match sets $2002.5 (sprite overflow); on hardware the
                // pointer then also increments m along with n while searching -
                // the diagonal-scan bug - so what counts as "in range" from here
                // is skewed and the flag has well-known false positives and
                // negatives (sprite_overflow_tests 2 / 4).
                if (inRange)
                {
                    StatusRegister.SpriteOverflow = true;
                    if (++_evalM == 4)
                    {
                        _evalM = 0;
                        AdvanceEvalN();
                    }
                }
                else
                {
                    AdvanceEvalN();
                    _evalM = (_evalM + 1) & 3;
                }
            }
            else if (inRange)
            {
                if (_evalN == 0)
                {
                    _evalSpriteZeroInRange = true;
                }
                _secondaryOamIndex++;
                _evalM = 1;
            }
            else
            {
                AdvanceEvalN();
            }
        }
        else
        {
            // Copying the tile / attribute / X bytes of an in-range sprite.
            if (!secondaryFull)
            {
                _secondaryOamIndex++;
            }
            if (++_evalM == 4)
            {
                _evalM = 0;
                AdvanceEvalN();
            }
        }
    }

    private void AdvanceEvalN()
    {
        if (++_evalN == 64)
        {
            _evalN = 0;
            _evalDone = true;
        }
    }

    // Dots 257-320: eight sprites x an 8-dot fetch each (garbage nametable,
    // garbage attribute, pattern low, pattern high), driving the multiplexed PPU
    // bus the same way BackgroundFetchTick does. Dot 321 is the trailing dot on
    // which the last sprite's pattern-high byte is finally on the bus.
    private void SpritePatternFetchCycle()
    {
        if (CurrentDot == 257)
        {
            // Publish the evaluation that just finished for the mux to use on the
            // next scanline, and clear the units the fetches below refill.
            _spriteCount = _secondaryOamIndex >> 2;
            _spriteZeroInRange = _evalSpriteZeroInRange;
            for (var i = 0; i < 8; i++)
            {
                _spritePatternShiftLo[i] = 0;
                _spritePatternShiftHi[i] = 0;
                _spriteAttributeLatches[i] = 0;
                _spriteXCounters[i] = 0xFF;
            }
        }

        var offset = (int)CurrentDot - 257;
        var slot = offset >> 3;
        var phase = offset & 7;

        // A slot's pattern-high byte lands on the bus one dot after its fetch
        // ends - the first dot of the next slot's window, or dot 321 for slot 7.
        // Latch it before anything new is driven onto the bus.
        if (phase == 0 && slot >= 1)
        {
            LoadSpriteUnit(slot - 1, _spriteFetchLatch, _adBus.Data);
        }

        if (slot >= 8)
        {
            return; // dot 321: only the trailing latch above.
        }

        switch (phase)
        {
            case 0: // Garbage nametable fetch.
                BeginVramFetch((ushort)(0x2000 | (_v & 0x0FFF)));
                break;

            case 1:
                EndVramFetch();
                break;

            case 2: // Garbage attribute fetch.
                BeginVramFetch((ushort)(
                    0x23C0 | (_v & 0x0C00) | ((_v >> 4) & 0x38) | ((_v >> 2) & 0x07)));
                break;

            case 3:
                EndVramFetch();
                break;

            case 4:
                BeginVramFetch(SpritePatternAddress(slot, high: false));
                break;

            case 5:
                EndVramFetch();
                break;

            case 6:
                _spriteFetchLatch = _adBus.Data;
                BeginVramFetch(SpritePatternAddress(slot, high: true));
                break;

            case 7:
                EndVramFetch();
                break;
        }
    }

    private ushort SpritePatternAddress(int slot, bool high)
    {
        var y = _secondaryOam[slot * 4 + 0];
        var tile = _secondaryOam[slot * 4 + 1];
        var attribute = _secondaryOam[slot * 4 + 2];
        var flipVertical = (attribute & 0x80) != 0;

        // CurrentScanline is still the evaluation line here; the same row the
        // range check used, which is one less than the line this pattern shows on
        // - that one-line lag is why a sprite draws at Y+1.
        var row = (int)CurrentScanline - y;

        ushort address;
        if (CtrlRegister.SpriteSize == SpriteSize.Size8x16)
        {
            if (flipVertical)
            {
                row = 15 - row;
            }

            var table = tile & 0x01;
            var tileIndex = tile & 0xFE;
            if (row >= 8)
            {
                tileIndex += 1;
                row -= 8;
            }

            address = (ushort)((table << 12) | (tileIndex << 4) | (row & 0x07));
        }
        else
        {
            if (flipVertical)
            {
                row = 7 - row;
            }

            address = (ushort)(
                (CtrlRegister.SpritePatternTableAddress << 12) | (tile << 4) | (row & 0x07));
        }

        return high ? (ushort)(address + 8) : address;
    }

    private void LoadSpriteUnit(int slot, byte patternLo, byte patternHi)
    {
        var attribute = _secondaryOam[slot * 4 + 2];

        if ((attribute & 0x40) != 0) // Horizontal flip: reverse the pattern bytes.
        {
            patternLo = ReverseBits(patternLo);
            patternHi = ReverseBits(patternHi);
        }

        _spritePatternShiftLo[slot] = patternLo;
        _spritePatternShiftHi[slot] = patternHi;
        _spriteAttributeLatches[slot] = attribute;
        _spriteXCounters[slot] = _secondaryOam[slot * 4 + 3];
    }

    private static byte ReverseBits(byte value)
    {
        value = (byte)(((value & 0xF0) >> 4) | ((value & 0x0F) << 4));
        value = (byte)(((value & 0xCC) >> 2) | ((value & 0x33) << 2));
        value = (byte)(((value & 0xAA) >> 1) | ((value & 0x55) << 1));
        return value;
    }
}
