using System;
using System.Numerics;
using Aemula.Emulation.Output;
using Hexa.NET.ImGui;
using Hexa.NET.SDL3;

namespace Aemula.UI;

// The composite-video texture-upload + aspect-correct blit shared by the two
// places that draw a Television's picture: the debugger's TelevisionWindow
// (which layers Saleae-style region overlays / crosshair / hover tooltip on
// top - see that class) and the borderless EmulationWindow (which shows only
// the picture). Both used to be the same copy-pasted CreateGpuResourcesForCurrentSize
// / PrepareOverride / DrawImage code; this is the one implementation they now
// share.
//
// Owns the GPU transfer buffer + texture for the Television's SampleBuffer at
// its current dimensions, and recreates *both* together whenever
// Television.Decode resizes SampleBuffer in place (the raster oscillators re-
// locking to slightly different detected line/frame timing - a normal,
// expected occurrence for this decoder, not a rare edge case): a transfer
// buffer allocated for the old, typically smaller, nominal size would silently
// overflow once Prepare tries to copy a larger SampleBuffer into it.
//
// Reads only the Television's own public state, once per UI frame - it has no
// idea what system is feeding the decoder.
public sealed class TelevisionTextureView : IDisposable
{
    // The on-screen rect DrawImage blitted the picture into, plus the
    // texture-space UV window it sampled from - between them, everything an
    // overlay needs to map a SampleBuffer (column, row) to a screen pixel.
    public readonly record struct ImagePlacement(Vector2 ImageMin, Vector2 ImageMax, Vector2 Uv0, Vector2 Uv1);

    private readonly Television _television;

    private SDLGPUDevicePtr _graphicsDevice;
    private SDLGPUTransferBufferPtr _transferBuffer;
    private SDLGPUTexturePtr _texture;
    private ImTextureRef _textureBinding;

    private uint _textureWidth, _textureHeight;

    // The transfer buffer's own allocated size, tracked separately from
    // SampleBuffer's *current* dimensions - the two only differ for the one
    // frame between a detected-timing resize and the next Prepare, but during
    // that frame this is what bounds the copy.
    private uint _transferBufferSizeInBytes;

    public TelevisionTextureView(Television television)
    {
        _television = television;
    }

    public ImTextureRef TextureBinding => _textureBinding;
    public uint TextureWidth => _textureWidth;
    public uint TextureHeight => _textureHeight;

    private SampleBuffer SampleBuffer => _television.SampleBuffer;

    public void CreateGraphicsResources(SDLGPUDevicePtr graphicsDevice)
    {
        _graphicsDevice = graphicsDevice;

        CreateGpuResourcesForCurrentSize();
    }

    // Allocates both the transfer buffer *and* the texture for SampleBuffer's
    // current dimensions, releasing whatever was there before. See this
    // class's own remarks on why both are recreated together rather than the
    // texture alone.
    private void CreateGpuResourcesForCurrentSize()
    {
        if (!_transferBuffer.IsNull)
        {
            SDL.ReleaseGPUTransferBuffer(_graphicsDevice, _transferBuffer);
        }

        if (!_texture.IsNull)
        {
            SDL.ReleaseGPUTexture(_graphicsDevice, _texture);
        }

        _textureWidth = SampleBuffer.Width;
        _textureHeight = SampleBuffer.Height;
        _transferBufferSizeInBytes = _textureWidth * _textureHeight * RgbaByte.SizeInBytes;

        _transferBuffer = SDL.CreateGPUTransferBuffer(
            _graphicsDevice,
            new SDLGPUTransferBufferCreateInfo(
                SDLGPUTransferBufferUsage.Upload,
                _transferBufferSizeInBytes));

        _texture = SDL.CreateGPUTexture(
            _graphicsDevice,
            new SDLGPUTextureCreateInfo
            {
                Type = SDLGPUTextureType.Texturetype2D,
                Format = SDLGPUTextureFormat.R8G8B8A8Unorm,
                Usage = (uint)SDLGPUTextureUsageFlags.Sampler,
                Width = _textureWidth,
                Height = _textureHeight,
                NumLevels = 1,
                SampleCount = SDLGPUSampleCount.Samplecount1,
                LayerCountOrDepth = 1,
            });

        unsafe
        {
            _textureBinding = new ImTextureRef(null, new ImTextureID(_texture));
        }
    }

    // Copies the current frame's SampleBuffer colors into the transfer buffer
    // and uploads them to the texture. Must run inside the same command buffer
    // the caller later renders ImGui draw data on.
    public void Prepare(SDLGPUCommandBufferPtr commandBuffer)
    {
        if (SampleBuffer.Width != _textureWidth || SampleBuffer.Height != _textureHeight)
        {
            CreateGpuResourcesForCurrentSize();
        }

        // Only Sample.Color is what the texture wants; the rest of each Sample
        // (Region and the diagnostic fields) stays behind, so this copies the
        // one field out a sample at a time rather than a plain memcpy of the
        // whole struct array.
        unsafe
        {
            var mapped = (RgbaByte*)SDL.MapGPUTransferBuffer(_graphicsDevice, _transferBuffer, false);
            var samples = SampleBuffer.Data;
            for (var i = 0; i < samples.Length; i++)
            {
                mapped[i] = samples[i].Color;
            }
        }

        SDL.UnmapGPUTransferBuffer(_graphicsDevice, _transferBuffer);

        var copyPass = SDL.BeginGPUCopyPass(commandBuffer);

        SDL.UploadToGPUTexture(
            copyPass,
            new SDLGPUTextureTransferInfo(_transferBuffer, pixelsPerRow: _textureWidth, rowsPerLayer: _textureHeight),
            new SDLGPUTextureRegion(_texture, w: _textureWidth, h: _textureHeight, d: 1),
            false);

        SDL.EndGPUCopyPass(copyPass);
    }

    // Draws the picture as an aspect-corrected ImGui.Image into the current
    // window's content region, and returns where it landed. With
    // activeVideoOnly the sync/blanking/color-burst raster is cropped out via
    // the image's own uv0/uv1 (a plain texture-sampling crop - SampleBuffer's
    // data is untouched either way), leaving just the picture; without it the
    // whole raster is shown. Either way the result is scaled to 4:3 using
    // Television.ComputeVerticalStretchFactor, since SampleBuffer is one raw
    // sample per column and one scanline per row - very much not square.
    public ImagePlacement DrawImage(bool activeVideoOnly)
    {
        // Needed both for the crop below and (crop or not) for the vertical-
        // stretch aspect math - computed once and reused. See
        // Television.ComputeActiveVideoRowRange.
        var (verticalActiveStart, verticalActiveCount) = _television.ComputeActiveVideoRowRange();

        Vector2 uv0, uv1;
        float displayedWidthSamples;
        float displayedHeightSamples;
        if (activeVideoOnly)
        {
            var activeStart = _television.ActiveVideoStartSamples;
            var activeEnd = activeStart + _television.ActiveVideoLengthSamples;
            uv0 = new Vector2(activeStart / _textureWidth, (float)verticalActiveStart / _textureHeight);
            uv1 = new Vector2(activeEnd / _textureWidth, (float)(verticalActiveStart + verticalActiveCount) / _textureHeight);
            displayedWidthSamples = _television.ActiveVideoLengthSamples;
            displayedHeightSamples = verticalActiveCount;
        }
        else
        {
            uv0 = Vector2.Zero;
            uv1 = Vector2.One;
            displayedWidthSamples = _textureWidth;
            displayedHeightSamples = _textureHeight;
        }

        var availableSize = ImGui.GetContentRegionAvail();
        var finalSize = CalculateSizeFittingAspectRatio(
            new Vector2(displayedWidthSamples, displayedHeightSamples * _television.ComputeVerticalStretchFactor(verticalActiveCount)),
            availableSize);

        // Center the picture in the window if there's extra space, rather than leaving it
        // in the top-left corner.
        var cursor = ImGui.GetCursorPos();
        ImGui.SetCursorPos(new Vector2(
            cursor.X + MathF.Max(0f, (availableSize.X - finalSize.X) * 0.5f),
            cursor.Y + MathF.Max(0f, (availableSize.Y - finalSize.Y) * 0.5f)));

        ImGui.Image(_textureBinding, finalSize, uv0, uv1);

        return new ImagePlacement(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), uv0, uv1);
    }

    private static Vector2 CalculateSizeFittingAspectRatio(
        in Vector2 boundsSize,
        in Vector2 viewportSize)
    {
        var ratioX = viewportSize.X / boundsSize.X;
        var ratioY = viewportSize.Y / boundsSize.Y;

        var ratio = ratioX < ratioY ? ratioX : ratioY;

        return boundsSize * ratio;
    }

    public void Dispose()
    {
        SDL.ReleaseGPUTexture(_graphicsDevice, _texture);
        SDL.ReleaseGPUTransferBuffer(_graphicsDevice, _transferBuffer);
    }
}
