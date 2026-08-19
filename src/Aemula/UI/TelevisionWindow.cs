using System;
using System.Numerics;
using Hexa.NET.ImGui;
using Hexa.NET.SDL3;

namespace Aemula.UI;

// This namespace nests under the root Aemula namespace, where the older,
// unrelated Aemula.Television already lives (see docs/television-plan.md's
// "Naming collision, explicitly out of scope" note) - plain enclosing-
// namespace lookup would find that one first, ahead of a plain `using
// Aemula.Emulation.Output;` placed above the namespace declaration (which is
// compilation-unit-scoped, and loses to an ancestor namespace's own member -
// see TelevisionTests.cs's remarks on the same issue), so it's aliased here
// instead, inside the namespace body, where it actually takes priority.
using Television = Aemula.Emulation.Output.Television;

// Phase 5 of docs/television-plan.md: a basic render only - without the
// dot-position/region overlays Phase 7 adds on top. The Television instance
// this renders gets fed samples elsewhere, live, during whatever system's
// emulation loop produced them (e.g. AppleIISystem.TickCompositeVideo calls
// Television.Decode directly, the same way any other signal propagates
// through the chips/systems that consume it) - this window only ever reads
// Television.DisplayBuffer, once per UI frame, and has no idea what's
// feeding it.
//
// Same overall GPU-texture-upload *shape* as ScreenDisplayWindow (allocate
// a transfer buffer + texture, map/upload/copy each frame, draw via
// ImGui.Image, release on Dispose), but a deliberately independent
// implementation - no shared base class or composition with it. Per the
// plan doc's "TelevisionWindow" section, ScreenDisplayWindow is slated for
// removal once this class replaces it, so tying the two together now would
// just create a removal headache later for no benefit today.
public sealed class TelevisionWindow : DebuggerWindow
{
    private readonly Television _television;

    private SDLGPUDevicePtr _graphicsDevice;
    private SDLGPUTransferBufferPtr _transferBuffer;
    private SDLGPUTexturePtr _texture;
    private ImTextureRef _textureBinding;

    private uint _textureWidth, _textureHeight;

    // The transfer buffer's own allocated size, tracked separately from
    // DisplayBuffer's *current* dimensions - see CreateGpuResourcesForCurrentSize's
    // remarks on why this can't just be recomputed live from DisplayBuffer
    // each frame the way _textureWidth/_textureHeight can.
    private uint _transferBufferSizeInBytes;

    public override string DisplayName => "Television";

    // Takes the whole Television instance, not just its DisplayBuffer
    // (unlike ScreenDisplayWindow) - the dot-position/region overlays added
    // in Phase 7 need CurrentColumn/CurrentRow/IsActiveVideo from the live
    // decoder, not just the pixels it already produced from them.
    public TelevisionWindow(Television television)
    {
        _television = television;
    }

    private DisplayBuffer DisplayBuffer => _television.DisplayBuffer;

    public override void CreateGraphicsResources(SDLGPUDevicePtr graphicsDevice)
    {
        base.CreateGraphicsResources(graphicsDevice);

        _graphicsDevice = graphicsDevice;

        CreateGpuResourcesForCurrentSize();
    }

    // Allocates both the transfer buffer *and* the texture for
    // DisplayBuffer's current dimensions, releasing whatever was there
    // before. Unlike ScreenDisplayWindow (which only ever recreates its
    // texture on a size change, and sizes its transfer buffer once, at
    // construction), this recreates *both* together: Television.Decode
    // resizes DisplayBuffer in place whenever the raster oscillators'
    // detected line/frame timing changes (see Television.cs) - a normal,
    // expected occurrence for this decoder, not a rare edge case - and a
    // transfer buffer allocated for the old (typically smaller, nominal)
    // size would silently overflow once PrepareOverride below tries to
    // memcpy a larger DisplayBuffer into it.
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

        _textureWidth = DisplayBuffer.Width;
        _textureHeight = DisplayBuffer.Height;
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

    protected override void PrepareOverride(EmulatorTime time, SDLGPUCommandBufferPtr commandBuffer)
    {
        if (DisplayBuffer.Width != _textureWidth || DisplayBuffer.Height != _textureHeight)
        {
            CreateGpuResourcesForCurrentSize();
        }

        unsafe
        {
            void* mapped = SDL.MapGPUTransferBuffer(_graphicsDevice, _transferBuffer, false);
            fixed (RgbaByte* pixelDataPtr = &DisplayBuffer.Data[0])
            {
                Buffer.MemoryCopy(pixelDataPtr, mapped, _transferBufferSizeInBytes, _transferBufferSizeInBytes);
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

    // A real broadcast picture's active area is conventionally 4:3, but
    // this texture is one raw sample per column and one scanline per row -
    // absolutely not the same physical size as each other, since Television's
    // horizontal sampling rate packs a scanline's active-video samples
    // (Television.ActiveVideoLengthSamples) into the same physical width a
    // real set devotes to a whole 4:3-shaped picture only
    // Television.NominalActiveLinesPerField lines tall. Rendered at native 1
    // sample:1 line square-pixel scaling, this comes out badly squashed into
    // a thin horizontal band instead of anything resembling a picture (both
    // of those properties are standard-specific - NTSC-only for now, same
    // seam as Television.Standard - which is exactly why this window reads
    // them from Television rather than knowing NTSC's own figures itself;
    // this stays correct with no changes here once a second standard
    // exists). This is purely a display-time correction (the same "non-
    // square pixel" adjustment real video tooling applies when showing a
    // broadcast-format capture on a square-pixel screen) - it stretches
    // only how large ImGui.Image draws the texture, not DisplayBuffer's
    // actual pixel data, which stays at native sample/line resolution for
    // Phase 7's overlays (and any other consumer that needs raw positions).
    private double VerticalStretchFactor =>
        (_television.ActiveVideoLengthSamples / _television.NominalActiveLinesPerField) / (4.0 / 3.0);

    protected override void DrawOverride(EmulatorTime time)
    {
        var availableSize = ImGui.GetContentRegionAvail();
        var finalSize = CalculateSizeFittingAspectRatio(
            new Vector2(_textureWidth, _textureHeight * (float)VerticalStretchFactor),
            availableSize);

        ImGui.Image(_textureBinding, finalSize);
    }

    private static Vector2 CalculateSizeFittingAspectRatio(
        in Vector2 boundsSize,
        in Vector2 viewportSize)
    {
        // Figure out the ratio.
        var ratioX = viewportSize.X / boundsSize.X;
        var ratioY = viewportSize.Y / boundsSize.Y;

        // Use whichever multiplier is smaller.
        var ratio = ratioX < ratioY ? ratioX : ratioY;

        return boundsSize * ratio;
    }

    public override void Dispose()
    {
        base.Dispose();

        SDL.ReleaseGPUTexture(_graphicsDevice, _texture);
        SDL.ReleaseGPUTransferBuffer(_graphicsDevice, _transferBuffer);
    }
}
