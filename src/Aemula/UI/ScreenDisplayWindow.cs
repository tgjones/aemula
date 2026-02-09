using System;
using System.Numerics;
using Hexa.NET.ImGui;
using Hexa.NET.SDL3;

namespace Aemula.UI;

public sealed class ScreenDisplayWindow : DebuggerWindow
{
    private readonly DisplayBuffer _displayBuffer;
    private readonly int _angle;

    private SDLGPUDevicePtr _graphicsDevice;
    private SDLGPUTransferBufferPtr _transferBuffer;
    private SDLGPUTexturePtr _texture;
    private ImTextureRef _textureBinding;

    private uint _textureWidth, _textureHeight;

    public override string DisplayName => "Display";

    public ScreenDisplayWindow(DisplayBuffer displayBuffer, int angle = 0)
    {
        _displayBuffer = displayBuffer;
        _angle = angle;
    }

    private uint TransferBufferSizeInBytes => _displayBuffer.Width * _displayBuffer.Height * RgbaByte.SizeInBytes;

    public override void CreateGraphicsResources(SDLGPUDevicePtr graphicsDevice)
    {
        base.CreateGraphicsResources(graphicsDevice);

        _graphicsDevice = graphicsDevice;

        _transferBuffer = SDL.CreateGPUTransferBuffer(
            graphicsDevice,
            new SDLGPUTransferBufferCreateInfo(
                SDLGPUTransferBufferUsage.Upload,
                TransferBufferSizeInBytes));

        CreateTexture();
    }

    private void CreateTexture()
    {
        if (!_texture.IsNull)
        {
            SDL.ReleaseGPUTexture(_graphicsDevice, _texture);
        }

        _texture = SDL.CreateGPUTexture(
            _graphicsDevice,
            new SDLGPUTextureCreateInfo
            {
                Type = SDLGPUTextureType.Texturetype2D,
                Format = SDLGPUTextureFormat.R8G8B8A8Unorm,
                Usage = (uint)SDLGPUTextureUsageFlags.Sampler,
                Width = _displayBuffer.Width,
                Height = _displayBuffer.Height,
                NumLevels = 1,
                SampleCount = SDLGPUSampleCount.Samplecount1,
                LayerCountOrDepth = 1,
            });

        _textureWidth = _displayBuffer.Width;
        _textureHeight = _displayBuffer.Height;

        unsafe
        {
            _textureBinding = new ImTextureRef(null, new ImTextureID(_texture));
        }
    }

    protected override void PrepareOverride(EmulatorTime time, SDLGPUCommandBufferPtr commandBuffer)
    {
        if (_displayBuffer.Width != _textureWidth || _displayBuffer.Height != _textureHeight)
        {
            CreateTexture();
        }

        unsafe
        {
            void* mapped = SDL.MapGPUTransferBuffer(_graphicsDevice, _transferBuffer, false);
            fixed (RgbaByte* pixelDataPtr = &_displayBuffer.Data[0])
            {
                Buffer.MemoryCopy(pixelDataPtr, mapped, TransferBufferSizeInBytes, TransferBufferSizeInBytes);
            }
        }

        SDL.UnmapGPUTransferBuffer(_graphicsDevice, _transferBuffer);

        //var commandBuffer = SDL.AcquireGPUCommandBuffer(_graphicsDevice);

        var copyPass = SDL.BeginGPUCopyPass(commandBuffer);

        SDL.UploadToGPUTexture(
            copyPass,
            new SDLGPUTextureTransferInfo(_transferBuffer, pixelsPerRow: _textureWidth, rowsPerLayer: _textureHeight),
            new SDLGPUTextureRegion(_texture, w: _textureWidth, h: _textureHeight, d: 1),
            false);

        SDL.EndGPUCopyPass(copyPass);

        //SDL.SubmitGPUCommandBuffer(commandBuffer);
    }

    protected override void DrawOverride(EmulatorTime time)
    {
        if (_angle == 0)
        {
            var availableSize = ImGui.GetContentRegionAvail();
            var finalSize = CalculateSizeFittingAspectRatio(
                new Vector2(_textureWidth, _textureHeight),
                availableSize);

            ImGui.Image(
                _textureBinding,
                finalSize);

            return;
        }

        var size = new Vector2(_textureHeight, _textureWidth);

        var cursorPos = ImGui.GetCursorPos();
        cursorPos.X += ImGui.GetContentRegionAvail().X / 2;
        cursorPos.Y += ImGui.GetWindowHeight() / 2;

        var uv0 = new Vector2(1, 0);
        var uv1 = new Vector2(1, 1);
        var uv2 = new Vector2(0, 1);
        var uv3 = new Vector2(0, 0);

        var p1 = cursorPos;
        var p2 = new Vector2(cursorPos.X + size.X, cursorPos.Y);
        var p3 = new Vector2(cursorPos.X + size.X, cursorPos.Y + size.Y);
        var p4 = new Vector2(cursorPos.X, cursorPos.Y + size.Y);

        ImGui.GetWindowDrawList().AddImageQuad(
            _textureBinding,
            p1, p2, p3, p4,
            uv0, uv1, uv2, uv3);
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
