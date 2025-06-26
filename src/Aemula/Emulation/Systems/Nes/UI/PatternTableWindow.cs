using System;
using System.Numerics;
using Aemula.Core;
using Aemula.Core.UI;
using ImGuiNET;
using Veldrid;

namespace Aemula.Systems.Nes.UI;

internal sealed class PatternTableWindow : DebuggerWindow
{
    private const int PatternTableSize = 128;
    private const int Scale = 2;

    private static readonly TimeSpan TextureUpdateInterval = TimeSpan.FromMilliseconds(200);

    private readonly Nes _nes;

    private readonly RgbaByte[] _pixelData0;
    private readonly RgbaByte[] _pixelData1;

    private GraphicsDevice _graphicsDevice;
    private Texture _patternTableTexture0, _patternTableTexture1;

    private IntPtr _patternTableTexture0Ptr, _patternTableTexture1Ptr;

    private TimeSpan _nextTextureUpdateTime;

    public override string DisplayName => "NES PPU Pattern Table";

    public override Pane PreferredPane => Pane.Bottom;

    public PatternTableWindow(Nes nes)
    {
        _nes = nes;

        _pixelData0 = new RgbaByte[PatternTableSize * PatternTableSize];
        _pixelData1 = new RgbaByte[PatternTableSize * PatternTableSize];
    }

    public override void CreateGraphicsResources(GraphicsDevice graphicsDevice, ImGuiRenderer renderer)
    {
        _graphicsDevice = graphicsDevice;

        Texture CreateTexture()
        {
            return graphicsDevice.ResourceFactory.CreateTexture(new TextureDescription(
                PatternTableSize, PatternTableSize,
                1, 1, 1,
                PixelFormat.R8_G8_B8_A8_UNorm,
                TextureUsage.Sampled,
                TextureType.Texture2D));
        }

        _patternTableTexture0 = CreateTexture();
        _patternTableTexture1 = CreateTexture();

        _patternTableTexture0Ptr = renderer.GetOrCreateImGuiBinding(graphicsDevice.ResourceFactory, _patternTableTexture0);
        _patternTableTexture1Ptr = renderer.GetOrCreateImGuiBinding(graphicsDevice.ResourceFactory, _patternTableTexture1);
    }

    protected override void DrawOverride(EmulatorTime time)
    {
        if (time.TotalTime > _nextTextureUpdateTime)
        {
            PopulateTexture(_pixelData0, _patternTableTexture0, (ushort)0x0000u);
            PopulateTexture(_pixelData1, _patternTableTexture1, (ushort)0x1000u);

            _nextTextureUpdateTime = time.TotalTime + TextureUpdateInterval;
        }

        var size = new Vector2(PatternTableSize * 2, PatternTableSize * Scale);

        ImGui.Image(_patternTableTexture0Ptr, size);
        ImGui.SameLine();
        ImGui.Image(_patternTableTexture1Ptr, size);
    }

    private void PopulateTexture(RgbaByte[] pixelData, Texture texture, ushort startAddress)
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

                var addressPlane0 = (ushort)(baseAddress);
                var addressPlane1 = (ushort)(baseAddress + 8);

                var dataPlane0 = _nes.ReadChrRom(addressPlane0);
                var dataPlane1 = _nes.ReadChrRom(addressPlane1);

                for (var column = 0; column < 8; column++)
                {
                    var pixelPlane0 = (dataPlane0 >> (7 - column)) & 1;
                    var pixelPlane1 = (dataPlane1 >> (7 - column)) & 1;

                    var pixel = pixelPlane0 | (pixelPlane1 << 1);

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

        _graphicsDevice.UpdateTexture(texture, pixelData, 0, 0, 0, PatternTableSize, PatternTableSize, 1, 0, 0);
    }
}
