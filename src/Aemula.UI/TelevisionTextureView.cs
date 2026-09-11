using System;
using System.Collections.Generic;
using System.Numerics;
using Aemula.Emulation.Output;
using Aemula.Emulation.Systems;
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

    // The active-video UV window and the 4:3-corrected on-screen size the
    // picture wants in its native (unrotated) orientation - the part
    // DrawImage and DrawImageRotated share. With activeVideoOnly the sync/
    // blanking/color-burst raster is cropped out via uv0/uv1 (a plain
    // texture-sampling crop - SampleBuffer's data is untouched either way),
    // leaving just the picture; without it the whole raster is described.
    // ContentSize is scaled to 4:3 using Television.ComputeVerticalStretchFactor,
    // since SampleBuffer is one raw sample per column and one scanline per
    // row - very much not square.
    private readonly record struct ActivePlacement(Vector2 Uv0, Vector2 Uv1, Vector2 ContentSize);

    private ActivePlacement ComputeActivePlacement(bool activeVideoOnly)
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

        var contentSize = new Vector2(
            displayedWidthSamples,
            displayedHeightSamples * _television.ComputeVerticalStretchFactor(verticalActiveCount));

        return new ActivePlacement(uv0, uv1, contentSize);
    }

    // Draws the picture as an aspect-corrected ImGui.Image into the current
    // window's content region, and returns where it landed.
    public ImagePlacement DrawImage(bool activeVideoOnly)
    {
        var placement = ComputeActivePlacement(activeVideoOnly);

        var availableSize = ImGui.GetContentRegionAvail();
        var finalSize = CalculateSizeFittingAspectRatio(placement.ContentSize, availableSize);

        // Center the picture in the window if there's extra space, rather than leaving it
        // in the top-left corner.
        var cursor = ImGui.GetCursorPos();
        ImGui.SetCursorPos(new Vector2(
            cursor.X + MathF.Max(0f, (availableSize.X - finalSize.X) * 0.5f),
            cursor.Y + MathF.Max(0f, (availableSize.Y - finalSize.Y) * 0.5f)));

        ImGui.Image(_textureBinding, finalSize, placement.Uv0, placement.Uv1);

        return new ImagePlacement(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), placement.Uv0, placement.Uv1);
    }

    // EmulationWindow's draw path: the active picture turned to the system's
    // cabinet orientation (rotation) with its colour gels (overlays)
    // multiplied over the monochrome pixels - a lit pixel takes the gel's
    // colour, an unlit one stays black, matching a transparent strip over an
    // emissive tube. The debugger's TelevisionWindow deliberately never calls
    // this: it wants the raw, unrotated, ungelled raster from DrawImage.
    //
    // Everything goes onto the window draw list as textured quads (rather
    // than ImGui.Image, which can't rotate) - the base picture once with the
    // active-video UVs mapped onto screen corners per the rotation, then each
    // gel as a re-blit of the same texture clipped to its screen sub-rect
    // with the gel colour as a multiply tint. A quarter-turn is axis-aligned
    // in both spaces, so every quad stays a rectangle and the tint math is
    // exact.
    public void DrawImageRotated(
        bool activeVideoOnly,
        ScreenRotation rotation,
        IReadOnlyList<ScreenOverlay> overlays)
    {
        var placement = ComputeActivePlacement(activeVideoOnly);

        // A quarter-turn swaps which content axis lands on screen width vs.
        // height; a half-turn or none leaves it be.
        var quarterTurned = rotation is ScreenRotation.Clockwise90 or ScreenRotation.Clockwise270;
        var boundsSize = quarterTurned
            ? new Vector2(placement.ContentSize.Y, placement.ContentSize.X)
            : placement.ContentSize;

        var availableSize = ImGui.GetContentRegionAvail();
        var finalSize = CalculateSizeFittingAspectRatio(boundsSize, availableSize);

        // Center it in the content region, same as DrawImage.
        var cursor = ImGui.GetCursorPos();
        ImGui.SetCursorPos(new Vector2(
            cursor.X + MathF.Max(0f, (availableSize.X - finalSize.X) * 0.5f),
            cursor.Y + MathF.Max(0f, (availableSize.Y - finalSize.Y) * 0.5f)));

        var origin = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        var uv0 = placement.Uv0;
        var uv1 = placement.Uv1;

        // The base picture: the whole player-facing rect, so its four screen
        // corners are (0,0)..(1,1) in display space, each mapped back through
        // the rotation to a texture UV. No tint.
        DrawTexturedRegion(drawList, origin, finalSize, rotation, uv0, uv1, 0f, 0f, 1f, 1f, 0xFFFFFFFFu);

        if (overlays != null)
        {
            foreach (var overlay in overlays)
            {
                var x0 = Math.Clamp(overlay.X, 0f, 1f);
                var y0 = Math.Clamp(overlay.Y, 0f, 1f);
                var x1 = Math.Clamp(overlay.X + overlay.Width, 0f, 1f);
                var y1 = Math.Clamp(overlay.Y + overlay.Height, 0f, 1f);
                if (x1 <= x0 || y1 <= y0)
                {
                    continue;
                }

                DrawTexturedRegion(drawList, origin, finalSize, rotation, uv0, uv1, x0, y0, x1, y1, GelTint(overlay.Color));
            }
        }

        // AddImageQuad advances no layout cursor - reserve the space so the
        // window sizes/centers around the picture the way it does for DrawImage.
        ImGui.Dummy(finalSize);
    }

    // Blits the sub-rect [x0,y0]-[x1,y1] of the player-facing picture (all in
    // 0..1 display space) as one textured quad: screen corners are that
    // rect's corners inside origin/finalSize, UV corners are those same
    // display points mapped back through the rotation into the active-video
    // UV window. col is the multiply tint.
    private void DrawTexturedRegion(
        ImDrawListPtr drawList,
        Vector2 origin,
        Vector2 finalSize,
        ScreenRotation rotation,
        Vector2 uv0,
        Vector2 uv1,
        float x0,
        float y0,
        float x1,
        float y1,
        uint col)
    {
        var pTL = origin + new Vector2(finalSize.X * x0, finalSize.Y * y0);
        var pTR = origin + new Vector2(finalSize.X * x1, finalSize.Y * y0);
        var pBR = origin + new Vector2(finalSize.X * x1, finalSize.Y * y1);
        var pBL = origin + new Vector2(finalSize.X * x0, finalSize.Y * y1);

        var uvTL = DisplayNormToUv(rotation, new Vector2(x0, y0), uv0, uv1);
        var uvTR = DisplayNormToUv(rotation, new Vector2(x1, y0), uv0, uv1);
        var uvBR = DisplayNormToUv(rotation, new Vector2(x1, y1), uv0, uv1);
        var uvBL = DisplayNormToUv(rotation, new Vector2(x0, y1), uv0, uv1);

        drawList.AddImageQuad(_textureBinding, pTL, pTR, pBR, pBL, uvTL, uvTR, uvBR, uvBL, col);
    }

    // Inverse of rotating the active-video region clockwise by `rotation` for
    // display: takes a point in player-facing 0..1 space (x right, y down)
    // and returns where it samples from inside the [uv0,uv1] window. (a, b)
    // is the position within that window, a across and b down.
    private static Vector2 DisplayNormToUv(ScreenRotation rotation, Vector2 d, Vector2 uv0, Vector2 uv1)
    {
        var (a, b) = rotation switch
        {
            ScreenRotation.Clockwise90 => (d.Y, 1f - d.X),
            ScreenRotation.Clockwise180 => (1f - d.X, 1f - d.Y),
            ScreenRotation.Clockwise270 => (1f - d.Y, d.X),
            _ => (d.X, d.Y),
        };

        return new Vector2(
            float.Lerp(uv0.X, uv1.X, a),
            float.Lerp(uv0.Y, uv1.Y, b));
    }

    // A colour gel as an ImGui multiply tint: white texel -> gel colour,
    // black texel -> black. Color.A scales the gel between "no tint" (0) and
    // "full colour" (255) by pulling each channel back toward 1.0.
    private static uint GelTint(RgbaByte color)
    {
        var strength = color.A / 255f;
        return ImGui.GetColorU32(new Vector4(
            1f + (color.R / 255f - 1f) * strength,
            1f + (color.G / 255f - 1f) * strength,
            1f + (color.B / 255f - 1f) * strength,
            1f));
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
