using System;
using System.Numerics;
using Aemula.UI;
using Hexa.NET.ImGui;
using Hexa.NET.SDL3;

namespace Aemula.Emulation.Systems.Nes.UI;

internal sealed class PatternTableWindow(NesSystem nes) : DebuggerWindow
{
    private const int PatternTableSize = 128;
    private const int Scale = 2;
    private const int TransferBufferSizeInBytes = PatternTableSize * PatternTableSize * RgbaByte.SizeInBytes;

    private static readonly TimeSpan TextureUpdateInterval = TimeSpan.FromMilliseconds(200);
    private readonly RgbaByte[] _pixelData0 = new RgbaByte[PatternTableSize * PatternTableSize];
    private readonly RgbaByte[] _pixelData1 = new RgbaByte[PatternTableSize * PatternTableSize];

    private SDLGPUDevicePtr _graphicsDevice;
    private SDLGPUTransferBufferPtr _transferBuffer;
    private SDLGPUTexturePtr _patternTableTexture0, _patternTableTexture1;

    private ImTextureRef _patternTableTexture0Ptr, _patternTableTexture1Ptr;

    private TimeSpan _nextTextureUpdateTime;

    public override string DisplayName => "NES PPU Pattern Table";

    public override Pane PreferredPane => Pane.Bottom;

    public override void CreateGraphicsResources(SDLGPUDevicePtr graphicsDevice)
    {
        _graphicsDevice = graphicsDevice;

        _transferBuffer = SDL.CreateGPUTransferBuffer(
            graphicsDevice,
            new SDLGPUTransferBufferCreateInfo(
                SDLGPUTransferBufferUsage.Upload,
                TransferBufferSizeInBytes));
        if (_transferBuffer.IsNull)
        {
            throw SDL.GetErrorAsException()!;
        }

        SDLGPUTexturePtr CreateTexture()
        {
            var textureInfo = new SDLGPUTextureCreateInfo
            {
                Type = SDLGPUTextureType.Texturetype2D,
                Format = SDLGPUTextureFormat.R8G8B8A8Unorm,
                Usage = (uint)SDLGPUTextureUsageFlags.Sampler,
                Width = PatternTableSize,
                Height = PatternTableSize,
                NumLevels = 1,
                SampleCount = SDLGPUSampleCount.Samplecount1,
                LayerCountOrDepth = 1,
            };

            var texture = SDL.CreateGPUTexture(graphicsDevice, textureInfo);
            if (texture.IsNull)
            {
                throw SDL.GetErrorAsException()!;
            }

            return texture;
        }

        _patternTableTexture0 = CreateTexture();
        _patternTableTexture1 = CreateTexture();

        unsafe
        {
            _patternTableTexture0Ptr = new ImTextureRef(null, new ImTextureID(_patternTableTexture0));
            _patternTableTexture1Ptr = new ImTextureRef(null, new ImTextureID(_patternTableTexture1));
        }
    }

    protected override void PrepareOverride(EmulatorTime time, SDLGPUCommandBufferPtr commandBuffer)
    {
        if (time.TotalTime > _nextTextureUpdateTime)
        {
            PopulateTexture(_pixelData0, _patternTableTexture0, (ushort)0x0000u, commandBuffer);
            PopulateTexture(_pixelData1, _patternTableTexture1, (ushort)0x1000u, commandBuffer);

            _nextTextureUpdateTime = time.TotalTime + TextureUpdateInterval;
        }
    }

    protected override void DrawOverride(EmulatorTime time)
    {
        var size = new Vector2(PatternTableSize * 2, PatternTableSize * Scale);

        ImGui.Image(_patternTableTexture0Ptr, size);
        ImGui.SameLine();
        ImGui.Image(_patternTableTexture1Ptr, size);
    }

    private void PopulateTexture(RgbaByte[] pixelData, SDLGPUTexturePtr texture, ushort startAddress, SDLGPUCommandBufferPtr commandBuffer)
    {
        var x = 0;
        var y = 0;

        for (var tileAddress = startAddress; tileAddress < startAddress + 0x0FFFu; tileAddress += 16)
        {
            if (tileAddress > startAddress && tileAddress % 256 == 0)
            {
                y += 8;
                x = 0;
            }

            var startX = x;

            for (var row = 0; row < 8; row++)
            {
                var baseAddress = tileAddress + row;

                var addressPlane0 = (ushort)baseAddress;
                var addressPlane1 = (ushort)(baseAddress + 8);

                var dataPlane0 = nes.ReadChrRom(addressPlane0);
                var dataPlane1 = nes.ReadChrRom(addressPlane1);

                for (var column = 0; column < 8; column++)
                {
                    var pixelPlane0 = dataPlane0 >> 7 - column & 1;
                    var pixelPlane1 = dataPlane1 >> 7 - column & 1;

                    var pixel = pixelPlane0 | pixelPlane1 << 1;

                    var gray = (byte)(pixel * 50);
                    var actualY = y + row;
                    var pixelIndex = actualY * PatternTableSize + x;
                    pixelData[pixelIndex] = new RgbaByte(gray, gray, gray, 255);

                    x++;
                }

                x = startX;
            }

            x += 8;
        }

        unsafe
        {
            void* mapped = SDL.MapGPUTransferBuffer(_graphicsDevice, _transferBuffer, true);
            if (mapped == null)
            {
                throw SDL.GetErrorAsException()!;
            }

            fixed (RgbaByte* pixelDataPtr = &pixelData[0])
            {
                Buffer.MemoryCopy(pixelDataPtr, mapped, TransferBufferSizeInBytes, TransferBufferSizeInBytes);
            }
        }

        SDL.UnmapGPUTransferBuffer(_graphicsDevice, _transferBuffer);

        var copyPass = SDL.BeginGPUCopyPass(commandBuffer);

        SDL.UploadToGPUTexture(
            copyPass,
            new SDLGPUTextureTransferInfo(_transferBuffer, pixelsPerRow: PatternTableSize, rowsPerLayer: PatternTableSize),
            new SDLGPUTextureRegion(texture, w: PatternTableSize, h: PatternTableSize, d: 1),
            false);

        SDL.EndGPUCopyPass(copyPass);
    }

    public override void Dispose()
    {
        SDL.ReleaseGPUTexture(_graphicsDevice, _patternTableTexture0);
        SDL.ReleaseGPUTexture(_graphicsDevice, _patternTableTexture1);

        SDL.ReleaseGPUTransferBuffer(_graphicsDevice, _transferBuffer);
    }
}
