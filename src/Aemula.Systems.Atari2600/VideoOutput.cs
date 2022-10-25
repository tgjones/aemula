﻿using System;
using Aemula.Chips.Tia;

namespace Aemula.Systems.Atari2600;

// This doesn't correlate to actual hardware in the Atari 2600.
// This is the way we gather video output from the TIA
// and put it into a framebuffer to display in an HTML canvas.
//
// Based on https://github.com/SavourySnaX/EDL/blob/a6a19f9db0a939230458d36bfe2715466cfad5d2/examples/2600/2600.c#L824
internal sealed class VideoOutput
{
    // For now, make space for EVERYTHING, even the vblank and hblank parts
    private const int Width = 160;

    private ushort _syncCounter;
    private ushort _currentScanline;
    private ushort _currentPos;
    private ushort _frame;

    private ushort _numVisibleScanlines;
    private ushort _viewportHeight;
    private ushort _lastTotalScanlines;
    private ushort _currentVisibleScanlines;
    private bool _scanlineContainedNonBlank;

    private uint[] _videoData;

    public VideoOutput()
    {
        // Assume there will be this many scanlines. If there are more, we'll resize our output.
        _numVisibleScanlines = 192;
        _viewportHeight = 192;
        _lastTotalScanlines = 192;

        CreateOutputBuffer();
    }

    private void CreateOutputBuffer()
    {
        _videoData = new uint[Width * _viewportHeight * 4];
    }

    public void Cycle(ref TiaPins tiaPins)
    {
        if (tiaPins.Sync)
        {
            _syncCounter++;
        }
        else
        {
            if (_syncCounter > 0)
            {
                if (_syncCounter > 300)
                {
                    if (_frame > 5 && _currentVisibleScanlines > _numVisibleScanlines)
                    {
                        _numVisibleScanlines = _currentVisibleScanlines;
                        _viewportHeight = Math.Min(_currentVisibleScanlines, (ushort)210);
                        CreateOutputBuffer();
                    }

                    // We just finished a vsync
                    _lastTotalScanlines = _currentScanline;
                    _currentScanline = 0;
                    _currentVisibleScanlines = 0;
                    _currentPos = 0;
                    _frame++;
                }
                else
                {
                    // We just finished a hsync
                    _currentScanline += 1;
                    _currentPos = 0;
                    if (_scanlineContainedNonBlank)
                    {
                        _currentVisibleScanlines++;
                    }
                    _scanlineContainedNonBlank = false;
                }
                _syncCounter = 0;
            }

            if (!tiaPins.Blk)
            {
                _scanlineContainedNonBlank = true;
            }

            if (tiaPins.Blk)
            {
                return;
            }

            var paletteIndex = tiaPins.Lum | (tiaPins.Col << 3);
            var color = Palette.NtscPalette[paletteIndex];

            var positionY = (int)Math.Round(_currentVisibleScanlines - ((_numVisibleScanlines - _viewportHeight) / 2.0f));

            var videoDataIndex = ((positionY * Width) + (_currentPos)) * 4;
            if (videoDataIndex > 0 && videoDataIndex < _videoData.Length)
            {
                _videoData[videoDataIndex + 0] = (color >> 0) & 0xFF;  // R
                _videoData[videoDataIndex + 1] = (color >> 8) & 0xFF;  // G
                _videoData[videoDataIndex + 2] = (color >> 16) & 0xFF; // B
                _videoData[videoDataIndex + 3] = 0xFF;                 // A
            }

            _currentPos++;
        }
    }
}
