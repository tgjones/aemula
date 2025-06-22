using System;
using Veldrid;

namespace Aemula.Core;

public sealed class Television
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

    public readonly DisplayBuffer DisplayBuffer;

    public Television()
    {
        // Assume there will be this many scanlines. If there are more, we'll resize our output.
        _numVisibleScanlines = 192;
        _viewportHeight = 192;
        _lastTotalScanlines = 192;

        DisplayBuffer = new DisplayBuffer(Width, _viewportHeight);
    }

    public void Signal(TelevisionSignal signal)
    {
        if (signal.Sync)
        {
            _syncCounter++;
            return;
        }

        if (_syncCounter > 0)
        {
            if (_syncCounter > 300)
            {
                if (_frame > 5 && _currentVisibleScanlines > _numVisibleScanlines)
                {
                    _numVisibleScanlines = _currentVisibleScanlines;
                    _viewportHeight = Math.Min(_currentVisibleScanlines, (ushort)260);
                    DisplayBuffer.Resize(Width, _viewportHeight);
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

        if (!signal.Blank)
        {
            _scanlineContainedNonBlank = true;
        }

        if (signal.Blank)
        {
            return;
        }

        var color = TelevisionPalette.NtscPalette[signal.Color];

        var positionY = (int)Math.Round(_currentVisibleScanlines - (_numVisibleScanlines - _viewportHeight) / 2.0f);

        var videoDataIndex = positionY * Width + _currentPos;
        if (videoDataIndex > 0 && videoDataIndex < Width * _viewportHeight)
        {
            DisplayBuffer.Data[videoDataIndex] = new RgbaByte(
                (byte)(color >> 16 & 0xFF), // R
                (byte)(color >> 8 & 0xFF),  // G
                (byte)(color >> 0 & 0xFF),  // B
                0xFF);                        // A
        }

        _currentPos++;
    }
}

public readonly record struct TelevisionSignal(
    bool Sync,
    bool Blank,
    bool ColorBurst,
    byte Color);

public sealed class DisplayBuffer
{
    public uint Width { get; private set; }
    public uint Height { get; private set; }

    public RgbaByte[] Data { get; private set; }

    public DisplayBuffer(uint width, uint height)
    {
        Resize(width, height);
    }

    public void Resize(uint width, uint height)
    {
        Width = width;
        Height = height;

        Data = new RgbaByte[width * height];

        for (var i = 0; i < Data.Length; i++)
        {
            Data[i] = new RgbaByte(0, 0, 0, 255);
        }
    }
}
